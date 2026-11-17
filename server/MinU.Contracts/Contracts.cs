namespace MinU.Contracts;

public enum ChatKind { Direct, Group }

public record RegisterRequest(string UserName, string DisplayName, string Password);
public record LoginRequest(string UserName, string Password);
/// <summary>
/// <see cref="Token"/> is short-lived and goes with every request; <see cref="RefreshToken"/> lasts weeks and only buys new tokens.
/// </summary>
public record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserDto User, string RefreshToken, DateTimeOffset RefreshExpiresAt);
public record RefreshRequest(string RefreshToken);

/// <summary><see cref="AvatarId"/> is the profile picture: a file fetched like any other, through a short-lived link.</summary>
public record UserDto(Guid Id, string UserName, string DisplayName, Guid? AvatarId = null);
public record UpdateProfileRequest(string DisplayName);

public record CreateDirectChatRequest(Guid UserId);
public record CreateGroupChatRequest(string Title, IReadOnlyList<Guid> MemberIds);
public record RenameGroupRequest(string Title);
public record AddMembersRequest(IReadOnlyList<Guid> UserIds);

/// <summary>
/// For a direct chat, <see cref="Title"/> is the other person's name.
/// <see cref="UnreadCount"/> counts the others' messages the viewer hasn't read yet;
/// <see cref="ReadUpTo"/> is the newest message someone else has read, so the viewer's own messages up to it show as read.
/// <see cref="OwnerId"/> is the group's owner, the only one who may remove others; null for a direct chat.
/// </summary>
public record ChatDto(Guid Id, ChatKind Kind, string Title, IReadOnlyList<UserDto> Members, MessageDto? LastMessage,
    int UnreadCount = 0, long ReadUpTo = 0, Guid? OwnerId = null);

/// <summary>
/// Whether someone is in the app right now. <see cref="LastSeenAt"/> is when they last left it, null if unknown.
/// Only people who share a chat see each other's presence.
/// </summary>
public record Presence(Guid UserId, bool Online, DateTimeOffset? LastSeenAt);

/// <summary><see cref="UserId"/> has read <see cref="ChatId"/> up to and including message <see cref="MessageId"/>.</summary>
public record ReadReceipt(Guid ChatId, Guid UserId, long MessageId);

/// <summary><see cref="System"/>: a change to a group, such as someone added, written by the server.</summary>
public enum MessageKind { Text, Call, System }

/// <summary>A call that has ended. <see cref="DurationSeconds"/> is null when nobody answered.</summary>
public record CallSummary(bool Video, int? DurationSeconds);

/// <summary>
/// <see cref="Id"/> grows within the server, so it orders messages and pages history.
/// A <see cref="MessageKind.Call"/> record comes from the server when a call ends; its <see cref="Sender"/> is the caller,
/// and <see cref="Text"/> describes it for apps that don't know call records.
/// </summary>
public record MessageDto(long Id, Guid ChatId, UserDto Sender, string Text, DateTimeOffset SentAt, Guid ClientMessageId,
    MessageKind Kind = MessageKind.Text, CallSummary? Call = null, IReadOnlyList<FileDto>? Attachments = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAttachments => Attachments is { Count: > 0 };

    /// <summary>One line for the chat list and notifications: the text, or what was attached when there is none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary
    {
        get
        {
            if (Attachments is not { Count: > 0 } files)
                return Text;
            var photos = files.Count(file => file.IsImage);
            var what = photos == files.Count
                ? photos == 1 ? "Фото" : $"Фото: {photos}"
                : files.Count == 1 ? $"Файл · {files[0].Name}" : $"Файлы: {files.Count}";
            return Text.Length > 0 ? $"{what} · {Text}" : what;
        }
    }
}

public static class AttachmentLimits
{
    /// <summary>How many files one message carries: an album of photos, say.</summary>
    public const int PerMessage = 10;
}

/// <summary>
/// STUN and TURN servers for calls: one ICE server per URL, all with these credentials.
/// Empty <see cref="Urls"/> when the server has no TURN configured; calls then only connect on a shared network.
/// </summary>
public record IceServersDto(IReadOnlyList<string> Urls, string Username, string Credential, DateTimeOffset ExpiresAt);

/// <summary><see cref="Platform"/> is "android" for Firebase Cloud Messaging, the only one served so far.</summary>
public record RegisterDeviceRequest(string Token, string Platform);

/// <summary>
/// Keys of the data in a push notification. Push carries only text pairs, and a data-only push
/// lets the app decide what to show, for example nothing while it is open.
/// </summary>
public static class PushKeys
{
    public const string Type = "type";
    public const string TypeMessage = "message";
    public const string TypeCall = "call";
    public const string TypeCallEnded = "call_ended";
    public const string TypeChatRead = "chat_read";

    public const string ChatId = "chatId";
    public const string ChatTitle = "chatTitle";
    public const string Sender = "sender";
    public const string Text = "text";
    public const string CallId = "callId";
    public const string Video = "video";
}

public record CallInvite(Guid CallId, Guid ChatId, UserDto Caller, bool Video);
public record CallParticipant(Guid CallId, string ParticipantId, UserDto User);
public record CallSignal(Guid CallId, string FromParticipantId, string Type, string Payload);

/// <summary>Whoever joins sends an offer to each of <see cref="Others"/>, who only answer, so offers never collide.</summary>
public record CallJoinResult(Guid CallId, string ParticipantId, IReadOnlyList<CallParticipant> Others);

/// <summary>
/// An uploaded file. Its bytes are fetched through a short-lived link, see <see cref="FileLinkDto"/>.
/// A picture has its size in pixels, for laying it out before it loads.
/// </summary>
public record FileDto(Guid Id, string Name, string ContentType, long Size, int? Width = null, int? Height = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Downloads the file until <see cref="ExpiresAt"/>, without any other sign-in; ask for a new one after that.</summary>
public record FileLinkDto(Uri Url, DateTimeOffset ExpiresAt);

public static class Limits
{
    /// <summary>The largest file a chat takes.</summary>
    public const long FileMax = 100L * 1024 * 1024;
    public const int FileNameMax = 255;
    public const int UserNameMin = 3;
    public const int UserNameMax = 32;
    public const int DisplayNameMax = 64;
    /// <summary>The largest profile picture; the app sends a small square one anyway.</summary>
    public const long AvatarMax = 5L * 1024 * 1024;
    public const int PasswordMin = 8;
    public const int ChatTitleMax = 100;
    public const int GroupMembersMax = 256;
    public const int MessageMax = 4000;
    public const int HistoryPageMax = 100;
    public const int DeviceTokenMax = 4096;
    /// <summary>How much of a message a push shows; the rest is read in the app.</summary>
    public const int PushTextMax = 200;
}

public static class CallLimits
{
    /// <summary>In a mesh call every device encodes video once per other participant, which limits the size.</summary>
    public const int MaxParticipants = 4;
}

/// <summary>Server-to-client events of the chat hub.</summary>
public interface IChatClient
{
    Task MessageReceived(MessageDto message);
    /// <summary>Goes to every member, so the reader's other devices clear the chat's unread count too.</summary>
    Task ChatRead(ReadReceipt receipt);
    /// <summary>Someone who shares a chat with this user came online or left.</summary>
    Task PresenceChanged(Presence presence);
    Task ChatCreated(ChatDto chat);
    /// <summary>
    /// A group's title, members or owner changed. Its unread count and last message aren't the viewer's own,
    /// so only those parts should be taken.
    /// </summary>
    Task ChatUpdated(ChatDto chat);
    /// <summary>The user left the group or was removed from it, on this device or another.</summary>
    Task ChatRemoved(Guid chatId);
    /// <summary>Someone who shares a chat with this user, or the user on another device, changed their name or picture.</summary>
    Task UserUpdated(UserDto user);
    Task CallInvited(CallInvite invite);
    Task CallParticipantJoined(CallParticipant participant);
    Task CallParticipantLeft(CallParticipant participant);
    Task CallDeclined(Guid callId, UserDto user);
    Task CallEnded(Guid callId);
    Task CallSignalReceived(CallSignal signal);
}
