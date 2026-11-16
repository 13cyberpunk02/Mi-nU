using Microsoft.EntityFrameworkCore;
using MinU.Contracts;
using Npgsql;

namespace MinU.Server;

public class ChatService(AppDbContext db, IFileStorage storage, ILogger<ChatService> logger)
{
    // The rows go for certain; a byte left behind in storage is only wasted space, so a failure there is just logged.
    async Task DeleteFilesAsync(Guid chatId)
    {
        var files = await db.Files.Where(file => file.ChatId == chatId).ToListAsync();
        await db.Files.Where(file => file.ChatId == chatId).ExecuteDeleteAsync();
        foreach (var file in files)
        {
            try
            {
                await storage.DeleteAsync(file.Key, CancellationToken.None);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                logger.LogWarning(e, "Could not delete file {Key} of deleted chat {ChatId}", file.Key, chatId);
            }
        }
    }

    public Task<bool> IsMemberAsync(Guid chatId, Guid userId) =>
        db.ChatMembers.AnyAsync(x => x.ChatId == chatId && x.UserId == userId);

    /// <summary>The group's name, or null for a direct chat, which goes by the other person's name.</summary>
    public Task<string?> GroupTitleAsync(Guid chatId) =>
        db.Chats.Where(chat => chat.Id == chatId && chat.Kind == ChatKind.Group).Select(chat => chat.Title).FirstOrDefaultAsync();

    public Task<List<Guid>> MemberIdsAsync(Guid chatId) =>
        db.ChatMembers.Where(x => x.ChatId == chatId).Select(x => x.UserId).ToListAsync();

    /// <summary>Everyone who shares at least one chat with the user: the people who may see their presence.</summary>
    public Task<List<Guid>> ContactIdsAsync(Guid userId) =>
        db.ChatMembers
            .Where(member => member.UserId != userId
                && db.ChatMembers.Any(mine => mine.UserId == userId && mine.ChatId == member.ChatId))
            .Select(member => member.UserId)
            .Distinct()
            .ToListAsync();

    public async Task<List<Presence>> PresenceOfContactsAsync(Guid userId, PresenceTracker presence)
    {
        var ids = await ContactIdsAsync(userId);
        var seen = await db.Users.Where(user => ids.Contains(user.Id)).Select(user => new { user.Id, user.LastSeenAt }).ToListAsync();
        return seen.Select(user => new Presence(user.Id, presence.IsOnline(user.Id), user.LastSeenAt)).ToList();
    }

    public async Task<List<ChatDto>> ChatsOfAsync(Guid userId)
    {
        var chats = await db.Chats
            .Where(chat => chat.Members.Any(member => member.UserId == userId))
            .Include(chat => chat.Members).ThenInclude(member => member.User)
            .AsNoTracking()
            .ToListAsync();

        var ids = chats.Select(chat => chat.Id).ToList();
        var lastIds = await db.Messages
            .Where(message => ids.Contains(message.ChatId))
            .GroupBy(message => message.ChatId)
            .Select(group => group.Max(message => message.Id))
            .ToListAsync();
        var last = await db.Messages
            .Where(message => lastIds.Contains(message.Id))
            .Include(message => message.Sender)
            .AsNoTracking()
            .ToDictionaryAsync(message => message.ChatId);
        var unread = await db.Messages
            .Where(message => message.SenderId != userId)
            .Join(db.ChatMembers.Where(member => member.UserId == userId),
                message => message.ChatId, member => member.ChatId,
                (message, member) => new { message.ChatId, message.Id, member.LastReadMessageId })
            .Where(x => x.Id > x.LastReadMessageId)
            .GroupBy(x => x.ChatId)
            .Select(group => new { ChatId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.ChatId, x => x.Count);
        var attachments = await AttachmentsOfAsync(lastIds);

        return chats
            .Select(chat => ToDto(chat, userId, last.GetValueOrDefault(chat.Id), unread.GetValueOrDefault(chat.Id), attachments))
            .OrderByDescending(chat => chat.LastMessage?.Id ?? 0)
            .ToList();
    }

    /// <summary>The files sent in each of the messages, in the order they were attached.</summary>
    async Task<Dictionary<long, List<FileDto>>> AttachmentsOfAsync(IReadOnlyCollection<long> messageIds)
    {
        if (messageIds.Count == 0)
            return [];
        var files = await db.Files.AsNoTracking()
            .Where(file => file.MessageId != null && messageIds.Contains(file.MessageId.Value))
            .OrderBy(file => file.CreatedAt)
            .ToListAsync();
        return files.GroupBy(file => file.MessageId!.Value).ToDictionary(group => group.Key, group => group.Select(ToDto).ToList());
    }

    public static FileDto ToDto(StoredFile file) => new(file.Id, file.Name, file.ContentType, file.Size, file.Width, file.Height);

    /// <summary>
    /// A message with files uploaded into the chat beforehand. They must be the sender's, in this chat, and not sent yet.
    /// Null when they aren't. Sending again with the same client id returns the message already sent.
    /// </summary>
    public async Task<(MessageDto? Message, bool Created)> AddMessageWithFilesAsync(Guid chatId, Guid senderId, string text,
        Guid clientMessageId, IReadOnlyCollection<Guid> fileIds)
    {
        if (await FindMessageAsync(senderId, clientMessageId) is { } existing)
            return (await WithAttachmentsAsync(existing), false);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var message = new Message { ChatId = chatId, SenderId = senderId, Text = text, ClientMessageId = clientMessageId };
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            db.ChangeTracker.Clear();
            return (await WithAttachmentsAsync((await FindMessageAsync(senderId, clientMessageId))!), false);
        }

        // One update that only takes files still free: two messages can't both claim the same file.
        var claimed = await db.Files
            .Where(file => fileIds.Contains(file.Id) && file.OwnerId == senderId && file.ChatId == chatId && file.MessageId == null)
            .ExecuteUpdateAsync(set => set.SetProperty(file => file.MessageId, message.Id));
        if (claimed != fileIds.Count)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            return (null, false);
        }
        await transaction.CommitAsync();

        await db.Entry(message).Reference(x => x.Sender).LoadAsync();
        await AdvanceReadAsync(chatId, senderId, message.Id);
        return (await WithAttachmentsAsync(message), true);
    }

    async Task<MessageDto> WithAttachmentsAsync(Message message) =>
        ToDto(message, (await AttachmentsOfAsync([message.Id])).GetValueOrDefault(message.Id));

    /// <summary>Returns the existing chat if the pair already has one; <c>Created</c> says which happened.</summary>
    public async Task<(Chat? Chat, bool Created)> GetOrCreateDirectAsync(Guid userId, Guid otherId)
    {
        var key = string.Join(':', new[] { userId, otherId }.Order());
        var existing = await FindDirectAsync(key);
        if (existing is not null)
            return (existing, false);
        if (userId == otherId || !await db.Users.AnyAsync(x => x.Id == otherId))
            return (null, false);

        var chat = new Chat
        {
            Kind = ChatKind.Direct,
            DirectKey = key,
            Members = [new ChatMember { UserId = userId }, new ChatMember { UserId = otherId }],
        };
        db.Chats.Add(chat);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // Both people opened the chat at the same moment and the other request won.
            db.ChangeTracker.Clear();
            return (await FindDirectAsync(key), false);
        }

        return (await LoadAsync(chat.Id), true);
    }

    public async Task<Chat?> CreateGroupAsync(Guid userId, string title, IEnumerable<Guid> memberIds)
    {
        var ids = memberIds.Append(userId).Distinct().ToList();
        if (await db.Users.CountAsync(x => ids.Contains(x.Id)) != ids.Count)
            return null;

        var chat = new Chat
        {
            Kind = ChatKind.Group,
            Title = title,
            OwnerId = userId,
            Members = ids.Select(id => new ChatMember { UserId = id }).ToList(),
        };
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        return await LoadAsync(chat.Id);
    }

    /// <summary>The group with its members, or null when it isn't a group or the user isn't in it.</summary>
    public Task<Chat?> FindGroupAsync(Guid chatId, Guid memberId) =>
        Chats().FirstOrDefaultAsync(chat => chat.Id == chatId && chat.Kind == ChatKind.Group
            && chat.Members.Any(member => member.UserId == memberId));

    public Task<Chat?> FindAsync(Guid chatId) => LoadAsync(chatId);

    public Task RenameAsync(Guid chatId, string title) =>
        db.Chats.Where(chat => chat.Id == chatId).ExecuteUpdateAsync(set => set.SetProperty(chat => chat.Title, title));

    /// <summary>Adds those of the users who exist and aren't members yet, and returns them.</summary>
    public async Task<List<User>> AddMembersAsync(Guid chatId, IEnumerable<Guid> userIds)
    {
        var ids = userIds.Distinct().ToList();
        var users = await db.Users
            .Where(user => ids.Contains(user.Id) && !db.ChatMembers.Any(member => member.ChatId == chatId && member.UserId == user.Id))
            .AsNoTracking()
            .ToListAsync();
        // They start with the history so far counted as read: it was written before they came.
        var newest = await db.Messages.Where(message => message.ChatId == chatId).MaxAsync(message => (long?)message.Id) ?? 0;
        db.ChatMembers.AddRange(users.Select(user => new ChatMember { ChatId = chatId, UserId = user.Id, LastReadMessageId = newest }));
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // Someone else added one of them at the same moment; whoever is missing can be added again.
            db.ChangeTracker.Clear();
            return [];
        }
        return users;
    }

    /// <summary>
    /// Takes the user out of the group. An owner who leaves hands the group to the member who has read it furthest,
    /// the one most likely still active in it; the last one to leave deletes it with its history.
    /// Returns whether the group still exists.
    /// </summary>
    public async Task<bool> RemoveMemberAsync(Guid chatId, Guid userId)
    {
        await db.ChatMembers.Where(member => member.ChatId == chatId && member.UserId == userId).ExecuteDeleteAsync();
        var remaining = await db.ChatMembers.Where(member => member.ChatId == chatId)
            .OrderByDescending(member => member.LastReadMessageId).Select(member => member.UserId).ToListAsync();
        if (remaining.Count == 0)
        {
            await db.Messages.Where(message => message.ChatId == chatId).ExecuteDeleteAsync();
            await DeleteFilesAsync(chatId);
            await db.Chats.Where(chat => chat.Id == chatId).ExecuteDeleteAsync();
            return false;
        }

        await db.Chats.Where(chat => chat.Id == chatId && chat.OwnerId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(chat => chat.OwnerId, remaining[0]));
        return true;
    }

    /// <summary>A line about a change to the group, from whoever made it.</summary>
    public async Task<MessageDto> AddSystemMessageAsync(Guid chatId, Guid actorId, string text)
    {
        var message = new Message { ChatId = chatId, SenderId = actorId, Text = text, ClientMessageId = Guid.NewGuid(), Kind = MessageKind.System };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        await db.Entry(message).Reference(x => x.Sender).LoadAsync();
        await AdvanceReadAsync(chatId, actorId, message.Id);
        return ToDto(message);
    }

    public async Task<List<MessageDto>> HistoryAsync(Guid chatId, long? beforeId, int limit)
    {
        var query = db.Messages.Where(message => message.ChatId == chatId);
        if (beforeId is { } before)
            query = query.Where(message => message.Id < before);

        var page = await query
            .OrderByDescending(message => message.Id)
            .Take(limit)
            .Include(message => message.Sender)
            .AsNoTracking()
            .ToListAsync();
        page.Reverse();
        var attachments = await AttachmentsOfAsync(page.Select(message => message.Id).ToList());
        return page.Select(message => ToDto(message, attachments.GetValueOrDefault(message.Id))).ToList();
    }

    public async Task<(MessageDto Message, bool Created)> AddMessageAsync(Guid chatId, Guid senderId, string text, Guid clientMessageId)
    {
        var existing = await FindMessageAsync(senderId, clientMessageId);
        if (existing is not null)
            return (ToDto(existing), false);

        var message = new Message { ChatId = chatId, SenderId = senderId, Text = text, ClientMessageId = clientMessageId };
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            db.ChangeTracker.Clear();
            return (ToDto((await FindMessageAsync(senderId, clientMessageId))!), false);
        }

        await db.Entry(message).Reference(x => x.Sender).LoadAsync();
        // Whoever writes has seen the chat up to their own message.
        await AdvanceReadAsync(chatId, senderId, message.Id);
        return (ToDto(message), true);
    }

    /// <summary>
    /// Puts an ended call into its chat, from the caller. Whoever took part has nothing to read in it,
    /// so it stays unread only for those who missed the call.
    /// </summary>
    public async Task<MessageDto> AddCallRecordAsync(CallRegistry.Call call, DateTimeOffset endedAt)
    {
        int? seconds = call.AnsweredAt is { } answered ? (int)Math.Max(0, Math.Round((endedAt - answered).TotalSeconds)) : null;
        var message = new Message
        {
            ChatId = call.ChatId,
            SenderId = call.Caller.Id,
            Text = CallText(call.Video, seconds),
            SentAt = endedAt,
            // The call's id: the record of a call can't be added twice.
            ClientMessageId = call.Id,
            Kind = MessageKind.Call,
            CallVideo = call.Video,
            CallSeconds = seconds,
        };
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            db.ChangeTracker.Clear();
            return ToDto((await FindMessageAsync(call.Caller.Id, call.Id))!);
        }

        await db.Entry(message).Reference(x => x.Sender).LoadAsync();
        foreach (var userId in call.Joined)
            await AdvanceReadAsync(call.ChatId, userId, message.Id);
        return ToDto(message);
    }

    /// <summary>The record's text for apps without call records; newer ones word it for each viewer.</summary>
    static string CallText(bool video, int? seconds) => seconds is { } s
        ? $"{(video ? "Видеозвонок" : "Звонок")}, {Duration(s)}"
        : video ? "Видеозвонок без ответа" : "Звонок без ответа";

    static string Duration(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>
    /// Moves the member's read mark forward to <paramref name="messageId"/>, never past the chat's newest message.
    /// Null when nothing changed: the mark was already there, or the chat is empty.
    /// </summary>
    public async Task<long?> MarkReadAsync(Guid chatId, Guid userId, long messageId)
    {
        var newest = await db.Messages.Where(message => message.ChatId == chatId).MaxAsync(message => (long?)message.Id) ?? 0;
        var upTo = Math.Min(messageId, newest);
        return upTo > 0 && await AdvanceReadAsync(chatId, userId, upTo) ? upTo : null;
    }

    // A single conditional update, so two devices reading at once can't move the mark backwards.
    async Task<bool> AdvanceReadAsync(Guid chatId, Guid userId, long messageId) =>
        await db.ChatMembers
            .Where(member => member.ChatId == chatId && member.UserId == userId && member.LastReadMessageId < messageId)
            .ExecuteUpdateAsync(set => set.SetProperty(member => member.LastReadMessageId, messageId)) > 0;

    public static ChatDto ToDto(Chat chat, Guid viewerId, Message? last, int unread = 0,
        IReadOnlyDictionary<long, List<FileDto>>? attachments = null)
    {
        var members = chat.Members.Select(member => ToDto(member.User)).ToList();
        var title = chat.Kind == ChatKind.Direct
            ? members.FirstOrDefault(member => member.Id != viewerId)?.DisplayName ?? ""
            : chat.Title ?? "";
        var readUpTo = chat.Members.Where(member => member.UserId != viewerId).Select(member => member.LastReadMessageId).DefaultIfEmpty().Max();
        var lastDto = last is null ? null : ToDto(last, attachments?.GetValueOrDefault(last.Id));
        return new ChatDto(chat.Id, chat.Kind, title, members, lastDto, unread, readUpTo, chat.OwnerId);
    }

    public static UserDto ToDto(User user) => new(user.Id, user.UserName, user.DisplayName);

    static MessageDto ToDto(Message message) => ToDto(message, null);

    static MessageDto ToDto(Message message, IReadOnlyList<FileDto>? attachments) =>
        new(message.Id, message.ChatId, ToDto(message.Sender), message.Text, message.SentAt, message.ClientMessageId,
            message.Kind, message.Kind == MessageKind.Call ? new CallSummary(message.CallVideo, message.CallSeconds) : null,
            attachments is { Count: > 0 } ? attachments : null);

    Task<Chat?> FindDirectAsync(string key) => Chats().FirstOrDefaultAsync(chat => chat.DirectKey == key);

    Task<Chat?> LoadAsync(Guid id) => Chats().FirstOrDefaultAsync(chat => chat.Id == id);

    IQueryable<Chat> Chats() =>
        db.Chats.Include(chat => chat.Members).ThenInclude(member => member.User).AsNoTracking();

    Task<Message?> FindMessageAsync(Guid senderId, Guid clientMessageId) =>
        db.Messages.Include(message => message.Sender).AsNoTracking()
            .FirstOrDefaultAsync(message => message.SenderId == senderId && message.ClientMessageId == clientMessageId);

    public static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
