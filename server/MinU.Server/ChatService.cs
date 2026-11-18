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

    public static FileDto ToDto(StoredFile file) => new(file.Id, file.Name, file.ContentType, file.Size, file.Width, file.Height, file.DurationSeconds, file.Waveform);

    /// <summary>
    /// A message with files uploaded into the chat beforehand. They must be the sender's, in this chat, and not sent yet.
    /// Null when they aren't. Sending again with the same client id returns the message already sent.
    /// </summary>
    public async Task<(MessageDto? Message, bool Created)> AddMessageWithFilesAsync(Guid chatId, Guid senderId, string text,
        Guid clientMessageId, IReadOnlyCollection<Guid> fileIds, long? replyToId = null)
    {
        if (await FindMessageAsync(senderId, clientMessageId) is { } existing)
            return (await WithAttachmentsAsync(existing), false);

        var replyTo = await ValidReplyAsync(chatId, replyToId);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var message = new Message { ChatId = chatId, SenderId = senderId, Text = text, ClientMessageId = clientMessageId, ReplyToId = replyTo };
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

    async Task<MessageDto> WithAttachmentsAsync(Message message) => await WithDetailsAsync(message);

    /// <summary>A message with everything about it: its files, its reactions, and the line it quotes if it is a reply.</summary>
    public async Task<MessageDto> WithDetailsAsync(Message message) => (await WithDetailsAsync([message]))[0];

    async Task<List<MessageDto>> WithDetailsAsync(IReadOnlyList<Message> messages)
    {
        var ids = messages.Select(message => message.Id).ToList();
        var files = await AttachmentsOfAsync(ids);
        var reactions = ids.Count == 0 ? [] : (await db.Reactions.AsNoTracking()
                .Where(reaction => ids.Contains(reaction.MessageId))
                .OrderBy(reaction => reaction.At)
                .ToListAsync())
            .GroupBy(reaction => reaction.MessageId)
            .ToDictionary(group => group.Key, group => group.GroupBy(reaction => reaction.Emoji)
                .Select(emoji => new ReactionDto(emoji.Key, emoji.Select(reaction => reaction.UserId).ToList()))
                .ToList());

        // The answered messages, each as one line: who wrote it and how it begins, or that it is gone.
        var answeredIds = messages.Where(message => message.ReplyToId is not null).Select(message => message.ReplyToId!.Value).Distinct().ToList();
        var answered = answeredIds.Count == 0 ? [] : await db.Messages.AsNoTracking().Include(message => message.Sender)
            .Where(message => answeredIds.Contains(message.Id))
            .ToListAsync();
        var answeredFiles = await AttachmentsOfAsync(answered.Select(message => message.Id).ToList());
        var quotes = answeredIds.ToDictionary(id => id, id => answered.FirstOrDefault(message => message.Id == id) is { } original
            ? new ReplyPreview(id, original.Sender.DisplayName, Shorten(ToDto(original, answeredFiles.GetValueOrDefault(id)).Summary))
            : new ReplyPreview(id, "", "", Deleted: true));

        return messages.Select(message => ToDto(message, files.GetValueOrDefault(message.Id)) with
        {
            ReplyTo = message.ReplyToId is { } replyTo ? quotes[replyTo] : null,
            Reactions = reactions.GetValueOrDefault(message.Id),
        }).ToList();
    }

    /// <summary>A quoted line is short: the start of a long message is enough to tell which one it was.</summary>
    static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= 100 ? line : line[..99] + "…";
    }

    /// <summary>The message a reply may answer: one in the same chat, or none.</summary>
    async Task<long?> ValidReplyAsync(Guid chatId, long? replyToId) =>
        replyToId is { } id && await db.Messages.AnyAsync(message => message.Id == id && message.ChatId == chatId) ? id : null;

    /// <summary>A message to change, with its chat: only the sender's own text or caption, never a call record or a group line.</summary>
    Task<Message?> OwnMessageAsync(Guid userId, long messageId) =>
        db.Messages.Include(message => message.Sender)
            .FirstOrDefaultAsync(message => message.Id == messageId && message.SenderId == userId && message.Kind == MessageKind.Text);

    /// <summary>New text for one's own message; a message with files may lose its caption, one without may not lose its text.</summary>
    public async Task<MessageDto?> EditAsync(Guid userId, long messageId, string text)
    {
        if (await OwnMessageAsync(userId, messageId) is not { } message)
            return null;
        if (text.Length == 0 && !await db.Files.AnyAsync(file => file.MessageId == messageId))
            return null;
        if (message.Text != text)
        {
            message.Text = text;
            message.EditedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        return await WithDetailsAsync(message);
    }

    /// <summary>Deletes one's own message for everyone, with its files and reactions. The chat it was in, or null.</summary>
    public async Task<Guid?> DeleteAsync(Guid userId, long messageId)
    {
        if (await OwnMessageAsync(userId, messageId) is not { } message)
            return null;
        var files = await db.Files.Where(file => file.MessageId == messageId).ToListAsync();
        db.Files.RemoveRange(files);
        db.Messages.Remove(message);
        await db.SaveChangesAsync();
        foreach (var file in files)
        {
            try
            {
                await storage.DeleteAsync(file.Key, CancellationToken.None);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                logger.LogWarning(e, "Could not delete file {Key} of deleted message {MessageId}", file.Key, messageId);
            }
        }
        return message.ChatId;
    }

    /// <summary>Sets or, with null, removes the user's reaction; the message as it now is, or null if it isn't theirs to see.</summary>
    public async Task<MessageDto?> ReactAsync(Guid userId, long messageId, string? emoji)
    {
        var message = await db.Messages.Include(x => x.Sender).AsNoTracking().FirstOrDefaultAsync(x => x.Id == messageId);
        if (message is null || message.Kind == MessageKind.System || !await IsMemberAsync(message.ChatId, userId))
            return null;
        await db.Reactions.Where(reaction => reaction.MessageId == messageId && reaction.UserId == userId).ExecuteDeleteAsync();
        if (emoji is not null)
        {
            db.Reactions.Add(new Reaction { MessageId = messageId, UserId = userId, Emoji = emoji });
            await db.SaveChangesAsync();
        }
        return await WithDetailsAsync(message);
    }

    /// <summary>
    /// A copy of a message in another chat the user is in, from them, saying who first wrote it. Its files are copied too,
    /// so deleting either message leaves the other whole. Null when the user can't see the message or can't write there.
    /// </summary>
    public async Task<MessageDto?> ForwardAsync(Guid userId, long messageId, Guid toChatId)
    {
        var original = await db.Messages.Include(x => x.Sender).AsNoTracking().FirstOrDefaultAsync(x => x.Id == messageId);
        if (original is null || original.Kind != MessageKind.Text
            || !await IsMemberAsync(original.ChatId, userId) || !await IsMemberAsync(toChatId, userId))
            return null;

        var copy = new Message
        {
            ChatId = toChatId,
            SenderId = userId,
            Text = original.Text,
            ClientMessageId = Guid.NewGuid(),
            ForwardedFrom = original.ForwardedFrom ?? original.Sender.DisplayName,
        };
        db.Messages.Add(copy);
        await db.SaveChangesAsync();

        var files = await db.Files.AsNoTracking().Where(file => file.MessageId == messageId).OrderBy(file => file.CreatedAt).ToListAsync();
        var copied = new List<StoredFile>();
        try
        {
            foreach (var file in files)
            {
                var twin = new StoredFile
                {
                    OwnerId = userId,
                    ChatId = toChatId,
                    MessageId = copy.Id,
                    Name = file.Name,
                    ContentType = file.ContentType,
                    Size = file.Size,
                    Width = file.Width,
                    Height = file.Height,
                    DurationSeconds = file.DurationSeconds,
                    Waveform = file.Waveform,
                    // Kept in the same order as the original's.
                    CreatedAt = file.CreatedAt,
                };
                await storage.CopyAsync(file.Key, twin.Key, CancellationToken.None);
                copied.Add(twin);
            }
        }
        catch (Exception e) when (e is Amazon.Runtime.AmazonServiceException or HttpRequestException)
        {
            logger.LogError(e, "Storage refused copying the files of message {MessageId}", messageId);
            foreach (var twin in copied)
                await storage.DeleteAsync(twin.Key, CancellationToken.None);
            await db.Messages.Where(message => message.Id == copy.Id).ExecuteDeleteAsync();
            throw;
        }
        db.Files.AddRange(copied);
        await db.SaveChangesAsync();

        await db.Entry(copy).Reference(x => x.Sender).LoadAsync();
        await AdvanceReadAsync(toChatId, userId, copy.Id);
        return await WithDetailsAsync(copy);
    }

    /// <summary>The chat's messages whose text holds <paramref name="query"/>, ignoring case, newest first.</summary>
    public async Task<List<MessageDto>> SearchAsync(Guid chatId, string query, int limit)
    {
        // % and _ are wildcards to LIKE; searched for, they mean themselves.
        var pattern = "%" + query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var found = await db.Messages.AsNoTracking().Include(message => message.Sender)
            .Where(message => message.ChatId == chatId && message.Kind == MessageKind.Text && EF.Functions.ILike(message.Text, pattern, @"\"))
            .OrderByDescending(message => message.Id)
            .Take(limit)
            .ToListAsync();
        return await WithDetailsAsync(found);
    }

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
        return await WithDetailsAsync(page);
    }

    public async Task<(MessageDto Message, bool Created)> AddMessageAsync(Guid chatId, Guid senderId, string text, Guid clientMessageId,
        long? replyToId = null)
    {
        var existing = await FindMessageAsync(senderId, clientMessageId);
        if (existing is not null)
            return (await WithDetailsAsync(existing), false);

        var message = new Message
        {
            ChatId = chatId, SenderId = senderId, Text = text, ClientMessageId = clientMessageId,
            ReplyToId = await ValidReplyAsync(chatId, replyToId),
        };
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            db.ChangeTracker.Clear();
            return (await WithDetailsAsync((await FindMessageAsync(senderId, clientMessageId))!), false);
        }

        await db.Entry(message).Reference(x => x.Sender).LoadAsync();
        // Whoever writes has seen the chat up to their own message.
        await AdvanceReadAsync(chatId, senderId, message.Id);
        return (await WithDetailsAsync(message), true);
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
        return new ChatDto(chat.Id, chat.Kind, title, members, lastDto, unread, readUpTo, chat.OwnerId, chat.AvatarFileId);
    }

    public static UserDto ToDto(User user) => new(user.Id, user.UserName, user.DisplayName, user.AvatarFileId);

    static MessageDto ToDto(Message message) => ToDto(message, null);

    static MessageDto ToDto(Message message, IReadOnlyList<FileDto>? attachments) =>
        new(message.Id, message.ChatId, ToDto(message.Sender), message.Text, message.SentAt, message.ClientMessageId,
            message.Kind, message.Kind == MessageKind.Call ? new CallSummary(message.CallVideo, message.CallSeconds) : null,
            attachments is { Count: > 0 } ? attachments : null, EditedAt: message.EditedAt, ForwardedFrom: message.ForwardedFrom);

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
