namespace MinU.Server;

/// <summary>
/// Who is online: a user is while at least one of their connections has the app in view.
/// Kept in memory like <see cref="CallRegistry"/>, so this server runs as a single instance.
/// </summary>
public class PresenceTracker
{
    readonly Lock _lock = new();
    // User id → their connections → whether the app is in view on that connection.
    readonly Dictionary<Guid, Dictionary<string, bool>> _users = [];

    public bool IsOnline(Guid userId)
    {
        lock (_lock)
            return Online(userId);
    }

    /// <summary>
    /// Records the connection's state; a null <paramref name="active"/> means it has closed.
    /// Returns the user's new state when it changed, otherwise null.
    /// </summary>
    public bool? Set(Guid userId, string connectionId, bool? active)
    {
        lock (_lock)
        {
            var before = Online(userId);
            if (!_users.TryGetValue(userId, out var connections))
                _users[userId] = connections = [];

            if (active is { } value)
                connections[connectionId] = value;
            else
                connections.Remove(connectionId);
            if (connections.Count == 0)
                _users.Remove(userId);

            var after = Online(userId);
            return after == before ? null : after;
        }
    }

    bool Online(Guid userId) => _users.TryGetValue(userId, out var connections) && connections.ContainsValue(true);
}
