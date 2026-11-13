using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MinU.Contracts;

namespace MinU.Server;

[Authorize]
public class ChatHub(ChatService chats, CallRegistry calls, AppDbContext db, PushQueue push) : Hub<IChatClient>
{
    Guid UserId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<MessageDto> SendMessage(Guid chatId, string text, Guid clientMessageId)
    {
        text = text.Trim();
        if (text.Length is 0 or > Limits.MessageMax)
            throw new HubException($"A message must be 1 to {Limits.MessageMax} characters long.");
        await RequireMemberAsync(chatId);

        var (message, created) = await chats.AddMessageAsync(chatId, UserId, text, clientMessageId);
        if (created)
        {
            var members = await chats.MemberIdsAsync(chatId);
            await Clients.Users(members.Select(id => id.ToString())).MessageReceived(message);
            push.Message(members.Where(id => id != UserId), message, await chats.GroupTitleAsync(chatId));
        }
        return message;
    }

    /// <summary>Joins the chat's running call if there is one; otherwise starts a call and rings the other members.</summary>
    public async Task<CallJoinResult> StartCall(Guid chatId, bool video)
    {
        await RequireMemberAsync(chatId);
        var me = await CurrentUserAsync();

        var (result, started) = calls.JoinOrStart(chatId, Context.ConnectionId, me);
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
        await base.OnDisconnectedAsync(exception);
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
