using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MinU.Contracts;

namespace MinU.Server;

[Authorize]
public class ChatHub(ChatService chats, CallRegistry calls, AppDbContext db, PushQueue push, PresenceTracker presence) : Hub<IChatClient>
{
    Guid UserId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // A new connection counts as in view; an app that connects in the background says otherwise with SetActive.
    public override async Task OnConnectedAsync()
    {
        await PresenceAsync(active: true);
        await base.OnConnectedAsync();
    }

    /// <summary>The app came into view or left it: switched away, minimized, or the phone locked.</summary>
    public Task SetActive(bool active) => PresenceAsync(active);

    /// <summary>Text, or files uploaded beforehand with an optional caption, optionally as a reply. Apps before replies use the two below.</summary>
    public Task<MessageDto> Send(SendMessageRequest request) =>
        request.FileIds is { Count: > 0 } files
            ? SendFilesAsync(request.ChatId, request.Text, request.ClientMessageId, files, request.ReplyToId)
            : SendTextAsync(request.ChatId, request.Text ?? "", request.ClientMessageId, request.ReplyToId);

    public Task<MessageDto> SendMessage(Guid chatId, string text, Guid clientMessageId) => SendTextAsync(chatId, text, clientMessageId, null);

    /// <summary>
    /// Sends files uploaded into the chat beforehand (POST /api/chats/{id}/files), with an optional caption.
    /// Resending with the same <paramref name="clientMessageId"/> after a lost connection doesn't send twice.
    /// </summary>
    public Task<MessageDto> SendAttachments(Guid chatId, string? text, Guid clientMessageId, IReadOnlyList<Guid> fileIds) =>
        SendFilesAsync(chatId, text, clientMessageId, fileIds, null);

    async Task<MessageDto> SendTextAsync(Guid chatId, string text, Guid clientMessageId, long? replyToId)
    {
        text = text.Trim();
        if (text.Length is 0 or > Limits.MessageMax)
            throw new HubException($"A message must be 1 to {Limits.MessageMax} characters long.");
        await RequireMemberAsync(chatId);

        var (message, created) = await chats.AddMessageAsync(chatId, UserId, text, clientMessageId, replyToId);
        if (created)
            await DeliverAsync(message);
        return message;
    }

    async Task<MessageDto> SendFilesAsync(Guid chatId, string? text, Guid clientMessageId, IReadOnlyList<Guid> fileIds, long? replyToId)
    {
        text = text?.Trim() ?? "";
        if (text.Length > Limits.MessageMax)
            throw new HubException($"A caption can be at most {Limits.MessageMax} characters long.");
        var files = fileIds?.Distinct().ToList() ?? [];
        if (files.Count is 0 or > AttachmentLimits.PerMessage)
            throw new HubException($"A message carries 1 to {AttachmentLimits.PerMessage} files.");
        await RequireMemberAsync(chatId);

        var (message, created) = await chats.AddMessageWithFilesAsync(chatId, UserId, text, clientMessageId, files, replyToId);
        if (message is null)
            throw new HubException("Those files can't be sent: they aren't yours, belong to another chat, or were sent already.");
        if (created)
            await DeliverAsync(message);
        return message;
    }

    /// <summary>A new message to every member's open apps, and a notification to the others.</summary>
    async Task DeliverAsync(MessageDto message)
    {
        var members = await chats.MemberIdsAsync(message.ChatId);
        await Clients.Users(members.Select(id => id.ToString())).MessageReceived(message);
        push.Message(members.Where(id => id != UserId), message, await chats.GroupTitleAsync(message.ChatId));
    }

    /// <summary>New text for one's own message, or caption for one with files; everyone in the chat sees the change.</summary>
    public async Task<MessageDto> EditMessage(long messageId, string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length > Limits.MessageMax)
            throw new HubException($"A message can be at most {Limits.MessageMax} characters long.");
        var message = await chats.EditAsync(UserId, messageId, text)
            ?? throw new HubException("Only your own messages can be edited, and a message without files needs text.");
        await Clients.Users(await MemberKeysAsync(message.ChatId)).MessageUpdated(message);
        return message;
    }

    /// <summary>Deletes one's own message for everyone in the chat, with its files.</summary>
    public async Task DeleteMessage(long messageId)
    {
        var chatId = await chats.DeleteAsync(UserId, messageId) ?? throw new HubException("Only your own messages can be deleted.");
        await Clients.Users(await MemberKeysAsync(chatId)).MessageDeleted(chatId, messageId);
    }

    /// <summary>The user's reaction to a message, one of <see cref="Reactions.Allowed"/>; null takes it back.</summary>
    public async Task<MessageDto> React(long messageId, string? emoji)
    {
        if (emoji is not null && !Reactions.Allowed.Contains(emoji))
            throw new HubException("There is no such reaction.");
        var message = await chats.ReactAsync(UserId, messageId, emoji) ?? throw new HubException("No such message.");
        await Clients.Users(await MemberKeysAsync(message.ChatId)).MessageUpdated(message);
        return message;
    }

    /// <summary>Sends a copy of a message the user can see into another chat they are in, saying who wrote it first.</summary>
    public async Task<MessageDto> Forward(long messageId, Guid toChatId)
    {
        var message = await chats.ForwardAsync(UserId, messageId, toChatId)
            ?? throw new HubException("That message can't be forwarded there.");
        await DeliverAsync(message);
        return message;
    }

    /// <summary>The user is typing in the chat; the app says so every few seconds while they do. Nothing is stored.</summary>
    public async Task Typing(Guid chatId)
    {
        var members = await chats.MemberIdsAsync(chatId);
        if (!members.Contains(UserId))
            return;
        await Clients.Users(members.Where(id => id != UserId).Select(id => id.ToString())).UserTyping(chatId, UserId);
    }

    /// <summary>The user has seen the chat up to <paramref name="messageId"/>; the app calls this while the chat is on screen.</summary>
    public async Task MarkRead(Guid chatId, long messageId)
    {
        await RequireMemberAsync(chatId);
        if (await chats.MarkReadAsync(chatId, UserId, messageId) is not { } upTo)
            return;
        await Clients.Users(await MemberKeysAsync(chatId)).ChatRead(new ReadReceipt(chatId, UserId, upTo));
        push.ChatRead(UserId, chatId);
    }

    /// <summary>Joins the chat's running call if there is one; otherwise starts a call and rings the other members.</summary>
    public async Task<CallJoinResult> StartCall(Guid chatId, bool video)
    {
        await RequireMemberAsync(chatId);
        var me = await CurrentUserAsync();

        var (result, started) = calls.JoinOrStart(chatId, Context.ConnectionId, me, video);
        if (result is null)
            throw new HubException($"The call is full: at most {CallLimits.MaxParticipants} people can take part.");

        if (started)
        {
            var others = (await chats.MemberIdsAsync(chatId)).Where(id => id != me.Id).ToList();
            var invite = new CallInvite(result.CallId, chatId, me, video);
            await Clients.Users(others.Select(id => id.ToString())).CallInvited(invite);
            // Rings phones whose app is closed; an open app answers through the event above instead.
            push.Call(others, invite);
        }
        else
        {
            await NotifyJoinedAsync(result, me);
        }
        return result;
    }

    public async Task<CallJoinResult> JoinCall(Guid callId)
    {
        await RequireMemberAsync(calls.ChatOf(callId) ?? throw new HubException("The call has ended."));
        var me = await CurrentUserAsync();

        var result = calls.Join(callId, Context.ConnectionId, me)
            ?? throw new HubException($"The call is full: at most {CallLimits.MaxParticipants} people can take part.");
        await NotifyJoinedAsync(result, me);
        return result;
    }

    public async Task DeclineCall(Guid callId)
    {
        await RequireMemberAsync(calls.ChatOf(callId) ?? throw new HubException("The call has ended."));
        await Clients.Clients(calls.ParticipantIds(callId)).CallDeclined(callId, await CurrentUserAsync());
    }

    public async Task LeaveCall(Guid callId)
    {
        if (calls.Leave(callId, Context.ConnectionId) is { } departure)
            await NotifyLeftAsync(departure);
    }

    public async Task SendCallSignal(Guid callId, string toParticipantId, string type, string payload)
    {
        if (!calls.AreInSameCall(callId, Context.ConnectionId, toParticipantId))
            throw new HubException("That participant is not in this call.");
        await Clients.Client(toParticipantId).CallSignalReceived(new CallSignal(callId, Context.ConnectionId, type, payload));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var departure in calls.LeaveAll(Context.ConnectionId))
            await NotifyLeftAsync(departure);
        await PresenceAsync(active: null);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Records this connection's state and tells the user's contacts if that changes whether they're online.</summary>
    async Task PresenceAsync(bool? active)
    {
        var me = UserId;
        if (presence.Set(me, Context.ConnectionId, active) is not { } online)
            return;

        DateTimeOffset? lastSeen = null;
        if (!online)
        {
            lastSeen = DateTimeOffset.UtcNow;
            await db.Users.Where(user => user.Id == me).ExecuteUpdateAsync(set => set.SetProperty(user => user.LastSeenAt, lastSeen));
        }
        var contacts = await chats.ContactIdsAsync(me);
        await Clients.Users(contacts.Select(id => id.ToString())).PresenceChanged(new Presence(me, online, lastSeen));
    }

    Task NotifyJoinedAsync(CallJoinResult result, UserDto me) =>
        Clients.Clients(result.Others.Select(other => other.ParticipantId))
            .CallParticipantJoined(new CallParticipant(result.CallId, result.ParticipantId, me));

    async Task NotifyLeftAsync(CallRegistry.Departure departure)
    {
        if (departure.Ended)
        {
            // Sent to the whole chat, so that phones still ringing stop, closed apps included.
            var members = await chats.MemberIdsAsync(departure.Call.ChatId);
            await Clients.Users(members.Select(id => id.ToString())).CallEnded(departure.Call.Id);
            push.CallEnded(members, departure.Call.Id);

            var call = departure.Call;
            var record = await chats.AddCallRecordAsync(call, DateTimeOffset.UtcNow);
            await Clients.Users(members.Select(id => id.ToString())).MessageReceived(record);
            // Whoever never picked up gets a missed call notification in place of the stopped ringing.
            var missed = members.Where(id => !call.Joined.Contains(id)).ToList();
            var text = call.Video ? "Пропущенный видеозвонок" : "Пропущенный звонок";
            push.Message(missed, record with { Text = text }, await chats.GroupTitleAsync(call.ChatId));
        }
        else
            await Clients.Clients(departure.Call.Participants.Keys.ToList()).CallParticipantLeft(departure.Participant);
    }

    async Task RequireMemberAsync(Guid chatId)
    {
        if (!await chats.IsMemberAsync(chatId, UserId))
            throw new HubException("You are not a member of this chat.");
    }

    async Task<UserDto> CurrentUserAsync() =>
        ChatService.ToDto(await db.Users.AsNoTracking().FirstAsync(user => user.Id == UserId));

    async Task<IEnumerable<string>> MemberKeysAsync(Guid chatId) =>
        (await chats.MemberIdsAsync(chatId)).Select(id => id.ToString());
}
