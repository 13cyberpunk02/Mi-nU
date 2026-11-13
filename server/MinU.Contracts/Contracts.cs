namespace MinU.Contracts;

public enum ChatKind { Direct, Group }

public record RegisterRequest(string UserName, string DisplayName, string Password);
public record LoginRequest(string UserName, string Password);
public record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);

public record UserDto(Guid Id, string UserName, string DisplayName);

public record CreateDirectChatRequest(Guid UserId);
public record CreateGroupChatRequest(string Title, IReadOnlyList<Guid> MemberIds);

/// <summary>For a direct chat, <see cref="Title"/> is the other person's name.</summary>
public record ChatDto(Guid Id, ChatKind Kind, string Title, IReadOnlyList<UserDto> Members, MessageDto? LastMessage);

/// <summary><see cref="Id"/> grows within the server, so it orders messages and pages history.</summary>
public record MessageDto(long Id, Guid ChatId, UserDto Sender, string Text, DateTimeOffset SentAt, Guid ClientMessageId);

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

public static class Limits
{
    public const int UserNameMin = 3;
    public const int UserNameMax = 32;
    public const int DisplayNameMax = 64;
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
    Task ChatCreated(ChatDto chat);
    Task CallInvited(CallInvite invite);
    Task CallParticipantJoined(CallParticipant participant);
    Task CallParticipantLeft(CallParticipant participant);
    Task CallDeclined(Guid callId, UserDto user);
    Task CallEnded(Guid callId);
    Task CallSignalReceived(CallSignal signal);
}
