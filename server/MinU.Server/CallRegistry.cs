using MinU.Contracts;

namespace MinU.Server;

/// <summary>Calls in progress. They live in memory, so this server can only run as a single instance.</summary>
public class CallRegistry
{
    public record Call(Guid Id, Guid ChatId, Dictionary<string, UserDto> Participants);
    public record Departure(Call Call, CallParticipant Participant, bool Ended);

    readonly Lock _lock = new();
    readonly Dictionary<Guid, Call> _calls = [];

    /// <summary>Joins the chat's call, starting it if there is none. <c>Started</c> is false when one was already running.</summary>
    public (CallJoinResult? Result, bool Started) JoinOrStart(Guid chatId, string connectionId, UserDto user)
    {
        lock (_lock)
        {
            var call = _calls.Values.FirstOrDefault(x => x.ChatId == chatId);
            var started = call is null;
            if (call is null)
            {
                call = new Call(Guid.NewGuid(), chatId, []);
                _calls.Add(call.Id, call);
            }

            return (Add(call, connectionId, user), started);
        }
    }

    /// <summary>Null if the call is over or full. The caller checks that the user belongs to the chat.</summary>
    public CallJoinResult? Join(Guid callId, string connectionId, UserDto user)
    {
        lock (_lock)
            return _calls.TryGetValue(callId, out var call) ? Add(call, connectionId, user) : null;
    }

    public Guid? ChatOf(Guid callId)
    {
        lock (_lock)
            return _calls.TryGetValue(callId, out var call) ? call.ChatId : null;
    }

    public IReadOnlyList<string> ParticipantIds(Guid callId)
    {
        lock (_lock)
            return _calls.TryGetValue(callId, out var call) ? [.. call.Participants.Keys] : [];
    }

    public bool AreInSameCall(Guid callId, string first, string second)
    {
        lock (_lock)
            return _calls.TryGetValue(callId, out var call)
                && call.Participants.ContainsKey(first)
                && call.Participants.ContainsKey(second);
    }

    public Departure? Leave(Guid callId, string connectionId)
    {
        lock (_lock)
            return _calls.TryGetValue(callId, out var call) ? Remove(call, connectionId) : null;
    }

    public List<Departure> LeaveAll(string connectionId)
    {
        lock (_lock)
            return _calls.Values.ToList()
                .Select(call => Remove(call, connectionId))
                .OfType<Departure>()
                .ToList();
    }

    static CallJoinResult? Add(Call call, string connectionId, UserDto user)
    {
        if (!call.Participants.ContainsKey(connectionId) && call.Participants.Count >= CallLimits.MaxParticipants)
            return null;

        call.Participants[connectionId] = user;
        var others = call.Participants
            .Where(x => x.Key != connectionId)
            .Select(x => new CallParticipant(call.Id, x.Key, x.Value))
            .ToList();
        return new CallJoinResult(call.Id, connectionId, others);
    }

    Departure? Remove(Call call, string connectionId)
    {
        if (!call.Participants.Remove(connectionId, out var user))
            return null;

        var ended = call.Participants.Count == 0;
        if (ended)
            _calls.Remove(call.Id);
        return new Departure(call, new CallParticipant(call.Id, connectionId, user), ended);
    }
}
