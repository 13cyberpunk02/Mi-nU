using Microsoft.EntityFrameworkCore;
using MinU.Contracts;

namespace MinU.Server;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserName { get; set; }
    public required string NormalizedUserName { get; set; }
    public required string DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Chat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChatKind Kind { get; set; }
    public string? Title { get; set; }
    /// <summary>Both user ids in a fixed order; its unique index allows one direct chat per pair.</summary>
    public string? DirectKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMember> Members { get; set; } = [];
}

public class ChatMember
{
    public Guid ChatId { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}

public class Message
{
    public long Id { get; set; }
    public Guid ChatId { get; set; }
    public Guid SenderId { get; set; }
    public User Sender { get; set; } = null!;
    public required string Text { get; set; }
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid ClientMessageId { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMember> ChatMembers => Set<ChatMember>();
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(user =>
        {
            user.Property(x => x.UserName).HasMaxLength(Limits.UserNameMax);
            user.Property(x => x.NormalizedUserName).HasMaxLength(Limits.UserNameMax);
            user.Property(x => x.DisplayName).HasMaxLength(Limits.DisplayNameMax);
            user.HasIndex(x => x.NormalizedUserName).IsUnique();
        });

        model.Entity<Chat>(chat =>
        {
            chat.Property(x => x.Title).HasMaxLength(Limits.ChatTitleMax);
            chat.HasIndex(x => x.DirectKey).IsUnique();
        });

        model.Entity<ChatMember>(member =>
        {
            member.HasKey(x => new { x.ChatId, x.UserId });
            member.HasIndex(x => x.UserId);
        });

        model.Entity<Message>(message =>
        {
            message.Property(x => x.Text).HasMaxLength(Limits.MessageMax);
            message.HasIndex(x => new { x.ChatId, x.Id });
            // A client resends with the same id after a lost connection; the index keeps that from duplicating.
            message.HasIndex(x => new { x.SenderId, x.ClientMessageId }).IsUnique();
        });
    }
}
