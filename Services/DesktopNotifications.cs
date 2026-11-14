using MinU.Contracts;
#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#endif

namespace Mi_nU.Services
{
    /// <summary>
    /// System notifications while the app runs but its window isn't in front: Windows has no push for a desktop app
    /// outside the Store, so these come from SignalR events the running app already receives.
    /// A tap goes through the same <see cref="PushNotifications"/> actions as a push on Android.
    /// </summary>
    public class DesktopNotifications
    {
        const string CallsGroup = "calls";

        readonly AuthService _auth;
        readonly CallService _calls;
        readonly Dictionary<Guid, ChatDto> _chats = [];
        Guid? _ringing;
        bool _registered;

        public DesktopNotifications(AuthService auth, ChatHubClient hub, CallService calls)
        {
            _auth = auth;
            _calls = calls;
            hub.MessageReceived += message => MainThread.BeginInvokeOnMainThread(() => OnMessage(message));
            hub.ChatCreated += chat => MainThread.BeginInvokeOnMainThread(() => _chats[chat.Id] = chat);
            calls.IncomingCall += OnIncomingCall;
            calls.Finished += _ => StopRinging();
        }

        /// <summary>Whether the app's window is in front; set from its Activated and Deactivated events.</summary>
        public bool WindowActive { get; set; } = true;

        /// <summary>Chat names for message notifications; the chat list hands them over when it loads.</summary>
        public void Remember(IEnumerable<ChatDto> chats)
        {
            foreach (var chat in chats)
                _chats[chat.Id] = chat;
        }

        public void Start()
        {
#if WINDOWS
            // Not for an app run as administrator: Windows App SDK refuses notifications to elevated processes.
            if (_registered || !AppNotificationManager.IsSupported())
                return;

            // Before Register, so a tap on a notification shown earlier in this run still arrives.
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
                MainThread.BeginInvokeOnMainThread(() => OnInvoked(e.Arguments));
            AppNotificationManager.Default.Register();
            _registered = true;
#endif
        }

        /// <summary>An app outside the Store must give back its registration on exit.</summary>
        public void Stop()
        {
#if WINDOWS
            if (_registered)
                AppNotificationManager.Default.Unregister();
            _registered = false;
#endif
        }

        void OnMessage(MessageDto message)
        {
            if (WindowActive || !_registered || message.Sender.Id == _auth.Session?.User.Id)
                return;

            var group = _chats.TryGetValue(message.ChatId, out var chat) && chat.Kind == ChatKind.Group ? chat.Title : null;
#if WINDOWS
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "open_chat")
                .AddArgument("chat", message.ChatId.ToString())
                .AddText(group is null ? message.Sender.DisplayName : $"{message.Sender.DisplayName} · {group}")
                .AddText(message.Text)
                // One notification per chat: a new message replaces the previous one instead of piling up.
                .SetTag(message.ChatId.ToString())
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
#else
            _ = group;
#endif
        }

        void OnIncomingCall()
        {
            if (WindowActive || !_registered || _calls.Invite is not { } invite)
                return;

            _ringing = invite.CallId;
#if WINDOWS
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "show_call")
                .AddText(invite.Caller.DisplayName)
                .AddText(invite.Video ? "Входящий видеозвонок" : "Входящий звонок")
                // Rings and stays on screen until answered, as the system's own calls do.
                .SetScenario(AppNotificationScenario.IncomingCall)
                .AddButton(new AppNotificationButton("Ответить").AddArgument("action", "answer"))
                .AddButton(new AppNotificationButton("Отклонить").AddArgument("action", "decline"))
                .SetTag(invite.CallId.ToString())
                .SetGroup(CallsGroup)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
#endif
        }

        void StopRinging()
        {
            if (_ringing is not { } callId)
                return;

            _ringing = null;
#if WINDOWS
            if (_registered)
                _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(callId.ToString(), CallsGroup);
#endif
        }

        void OnInvoked(IDictionary<string, string> arguments)
        {
            BringWindowToFront();
            switch (arguments.TryGetValue("action", out var action) ? action : null)
            {
                case "open_chat" when arguments.TryGetValue("chat", out var chat) && Guid.TryParse(chat, out var chatId):
                    PushNotifications.Post(new PushAction.OpenChat(chatId));
                    break;
                case "answer" when _calls.Invite is { } invite:
                    PushNotifications.Post(new PushAction.IncomingCall(invite, Answer: true));
                    break;
                case "decline":
                    _ = DeclineAsync();
                    break;
                // "show_call": the incoming call screen is already open behind the window.
            }
        }

        async Task DeclineAsync()
        {
            await _calls.DeclineAsync();
            if (Shell.Current?.CurrentPage is Pages.IncomingCallPage)
                await Shell.Current.GoToAsync("..");
        }

        static void BringWindowToFront()
        {
#if WINDOWS
            if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
            {
                // A minimized window has to be restored first, or activating it changes nothing on screen.
                if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
                    presenter.Restore();
                window.Activate();
            }
#endif
        }
    }
}
