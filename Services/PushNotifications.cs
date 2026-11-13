using MinU.Contracts;

namespace Mi_nU.Services
{
    /// <summary>What the user chose on a notification, waiting until the app is signed in and connected.</summary>
    public abstract record PushAction
    {
        public sealed record OpenChat(Guid ChatId) : PushAction;
        public sealed record IncomingCall(CallInvite Invite, bool Answer) : PushAction;
    }

    /// <summary>
    /// Push notifications: registering this device with the server and acting on taps.
    /// Only Android has push for now; elsewhere this does nothing.
    /// </summary>
    public class PushNotifications(AuthService auth, ChatApi api)
    {
        const string TokenKey = "push_token";
        const string Platform = "android";

        static PushAction? _pending;

        /// <summary>Raised on the main thread when a tap left an action waiting.</summary>
        public static event Action? ActionWaiting;

        public static void Post(PushAction action) => MainThread.BeginInvokeOnMainThread(() =>
        {
            _pending = action;
            ActionWaiting?.Invoke();
        });

        public static PushAction? TakePending()
        {
            var action = _pending;
            _pending = null;
            return action;
        }

        /// <summary>Asks for permission to notify and tells the server this device's token. Safe to call on every sign-in.</summary>
        public async Task RegisterAsync()
        {
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                await Permissions.RequestAsync<Permissions.PostNotifications>();

            var token = await Droid.PushService.GetTokenAsync();
            if (token is null || auth.Session is null)
                return;

            Preferences.Default.Set(TokenKey, token);
            try
            {
                await api.RegisterDeviceAsync(token, Platform);
            }
            catch (ApiException)
            {
                // Next sign-in or app start tries again.
            }
#else
            await Task.CompletedTask;
#endif
        }

        /// <summary>Stops notifications for the signed-in user on this device; call before signing out.</summary>
        public async Task UnregisterAsync()
        {
            if (Preferences.Default.Get<string?>(TokenKey, null) is not { } token || auth.Session is null)
                return;

            try
            {
                await api.UnregisterDeviceAsync(token);
            }
            catch (ApiException)
            {
                // The server forgets the token anyway once someone else signs in on this phone.
            }
        }

        /// <summary>Firebase replaced the token: the server needs the new one.</summary>
        public void OnNewToken(string token)
        {
            Preferences.Default.Set(TokenKey, token);
            if (auth.Session is not null)
                _ = RegisterAsync();
        }
    }
}
