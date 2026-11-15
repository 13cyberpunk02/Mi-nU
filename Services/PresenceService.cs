using MinU.Contracts;

namespace Mi_nU.Services
{
    /// <summary>
    /// Who of the user's contacts is online, and the user's own presence reported to the server:
    /// online while the app is in view (see <see cref="AppActivity"/>).
    /// </summary>
    public class PresenceService
    {
        readonly ChatApi _api;
        readonly ChatHubClient _hub;
        readonly AppActivity _activity;
        readonly Dictionary<Guid, Presence> _known = [];

        public PresenceService(ChatApi api, ChatHubClient hub, AppActivity activity, AuthService auth)
        {
            _api = api;
            _hub = hub;
            _activity = activity;
            hub.PresenceChanged += presence => MainThread.BeginInvokeOnMainThread(() => Apply(presence));
            hub.Connected += () => MainThread.BeginInvokeOnMainThread(async () => await OnConnectedAsync());
            // A new chat may bring a new contact, whose presence isn't known yet.
            hub.ChatCreated += _ => MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());
            activity.Changed += () => _ = hub.SetActiveAsync(activity.IsActive);
            auth.SignedOut += () => _known.Clear();
        }

        /// <summary>A contact's presence changed. Raised on the main thread.</summary>
        public event Action<Guid>? Changed;

        public Presence? Of(Guid userId) => _known.GetValueOrDefault(userId);

        public bool IsOnline(Guid userId) => Of(userId)?.Online == true;

        /// <summary>"в сети", "был(а) в 18:40" and so on; empty when nothing is known.</summary>
        public static string Describe(Presence? presence, DateTimeOffset? now = null)
        {
            if (presence is null)
                return "";
            if (presence.Online)
                return "в сети";
            if (presence.LastSeenAt is not { } seen)
                return "";

            var ago = (now ?? DateTimeOffset.Now) - seen;
            var local = seen.ToLocalTime();
            if (ago < TimeSpan.FromMinutes(1))
                return "был(а) только что";
            if (ago < TimeSpan.FromHours(1))
                return $"был(а) {(int)ago.TotalMinutes} мин назад";
            if (local.Date == DateTime.Today)
                return $"был(а) в {local:HH:mm}";
            if (local.Date == DateTime.Today.AddDays(-1))
                return $"был(а) вчера в {local:HH:mm}";
            return $"был(а) {local.ToString(local.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy")}";
        }

        async Task OnConnectedAsync()
        {
            // The server counts a new connection as in view; one made in the background says otherwise.
            if (!_activity.IsActive)
                await _hub.SetActiveAsync(false);
            // Changes missed while disconnected only come back this way.
            await RefreshAsync();
        }

        async Task RefreshAsync()
        {
            try
            {
                foreach (var presence in await _api.GetPresenceAsync())
                    Apply(presence);
            }
            catch (ApiException)
            {
                // Offline: the next connect fetches it again.
            }
        }

        void Apply(Presence presence)
        {
            // Coming online carries no time, but the last one is still worth keeping.
            var previous = Of(presence.UserId);
            var merged = presence with { LastSeenAt = presence.LastSeenAt ?? previous?.LastSeenAt };
            if (merged == previous)
                return;

            _known[presence.UserId] = merged;
            Changed?.Invoke(presence.UserId);
        }
    }
}
