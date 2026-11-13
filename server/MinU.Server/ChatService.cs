using Microsoft.EntityFrameworkCore;
using MinU.Contracts;
using Npgsql;

namespace MinU.Server;

public class ChatService(AppDbContext db)
{
    public Task<bool> IsMemberAsync(Guid chatId, Guid userId) =>
        db.ChatMembers.AnyAsync(x => x.ChatId == chatId && x.UserId == userId);

    /// <summary>The group's name, or null for a direct chat, which goes by the other person's name.</summary>
    public Task<string?> GroupTitleAsync(Guid chatId) =>
        db.Chats.Where(chat => chat.Id == chatId && chat.Kind == ChatKind.Group).Select(chat => chat.Title).FirstOrDefaultAsync();

    public Task<List<Guid>> MemberIdsAsync(Guid chatId) =>
        db.ChatMembers.Where(x => x.ChatId == chatId).Select(x => x.UserId).ToListAsync();

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

        return chats
            .Select(chat => ToDto(chat, userId, last.GetValueOrDefault(chat.Id)))
            .OrderByDescending(chat => chat.LastMessage?.Id ?? 0)
            .ToList();
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
            Members = ids.Select(id => new ChatMember { UserId = id }).ToList(),
        };
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        return await LoadAsync(chat.Id);
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
        return page.Select(ToDto).ToList();
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
        return (ToDto(message), true);
    }

    public static ChatDto ToDto(Chat chat, Guid viewerId, Message? last)
    {
        var members = chat.Members.Select(member => ToDto(member.User)).ToList();
        var title = chat.Kind == ChatKind.Direct
            ? members.FirstOrDefault(member => member.Id != viewerId)?.DisplayName ?? ""
            : chat.Title ?? "";
        return new ChatDto(chat.Id, chat.Kind, title, members, last is null ? null : ToDto(last));
    }

    public static UserDto ToDto(User user) => new(user.Id, user.UserName, user.DisplayName);

    static MessageDto ToDto(Message message) =>
        new(message.Id, message.ChatId, ToDto(message.Sender), message.Text, message.SentAt, message.ClientMessageId);

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
