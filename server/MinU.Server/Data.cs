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
    /// <summary>When the user last left the app; null if they haven't since this was recorded.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>The profile picture, a <see cref="StoredFile"/> in no chat.</summary>
    public Guid? AvatarFileId { get; set; }
}

public class Chat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChatKind Kind { get; set; }
    public string? Title { get; set; }
    /// <summary>Both user ids in a fixed order; its unique index allows one direct chat per pair.</summary>
    public string? DirectKey { get; set; }
    /// <summary>A group's owner: whoever created it, or the member it passed to when they left.</summary>
    public Guid? OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMember> Members { get; set; } = [];
}

public class ChatMember
{
    public Guid ChatId { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    /// <summary>The newest message this member has seen; later messages from others are unread.</summary>
    public long LastReadMessageId { get; set; }
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
    public MessageKind Kind { get; set; }
    /// <summary>For a call record: whether it was a video call.</summary>
    public bool CallVideo { get; set; }
    /// <summary>For a call record: how long it lasted from the answer; null when nobody answered.</summary>
    public int? CallSeconds { get; set; }
}

/// <summary>Where to send a user's push notifications: one row per installed app.</summary>
public class DeviceToken
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public required string Token { get; set; }
    public required string Platform { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A sign-in on one device. Only a hash is stored, so a leaked database can't be used to sign in.
/// Each refresh replaces the token; seeing a replaced one again means it was copied, see Endpoints.RefreshAsync.
/// </summary>
public class RefreshToken
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public required string Hash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public bool Replaced { get; set; }
}

/// <summary>
/// A file someone uploaded; the bytes live in <see cref="IFileStorage"/> under <see cref="Key"/>.
/// Who may download it follows from <see cref="ChatId"/>: the chat's members.
/// </summary>
public class StoredFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid? ChatId { get; set; }
    public required string Name { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The message it was sent in; null while it is only uploaded.</summary>
    public long? MessageId { get; set; }
    /// <summary>A picture's size in pixels, as the uploading app measured it, so a chat can lay it out before it loads.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>The object's name in storage: random, so one can't be guessed from another.</summary>
    public string Key => Id.ToString("N");
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMember> ChatMembers => Set<ChatMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

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

        model.Entity<RefreshToken>(token =>
        {
            token.Property(x => x.Hash).HasMaxLength(64);
            token.HasIndex(x => x.Hash).IsUnique();
            token.HasIndex(x => x.UserId);
            token.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DeviceToken>(device =>
        {
            device.Property(x => x.Token).HasMaxLength(Limits.DeviceTokenMax);
            device.Property(x => x.Platform).HasMaxLength(16);
            // A token belongs to one installed app, so whoever signs in on it last owns it.
            device.HasIndex(x => x.Token).IsUnique();
            device.HasIndex(x => x.UserId);
            device.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<StoredFile>(file =>
        {
            file.Property(x => x.Name).HasMaxLength(Limits.FileNameMax);
            file.Property(x => x.ContentType).HasMaxLength(128);
            file.HasIndex(x => x.ChatId);
            file.HasIndex(x => x.OwnerId);
            file.HasIndex(x => x.MessageId);
            file.Ignore(x => x.Key);
        });
    }
}
