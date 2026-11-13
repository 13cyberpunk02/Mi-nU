using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU
{
    public partial class AppShell : Shell
    {
        readonly AuthService _auth;
        readonly ChatHubClient _hub;
        readonly CallService _calls;
        readonly ChatApi _api;

        public AppShell(AuthService auth, ChatHubClient hub, CallService calls, ChatApi api)
        {
            InitializeComponent();
            _auth = auth;
            _hub = hub;
            _calls = calls;
            _api = api;
            Routing.RegisterRoute("register", typeof(Pages.RegisterPage));
            Routing.RegisterRoute("chat", typeof(Pages.ChatPage));
            Routing.RegisterRoute("newchat", typeof(Pages.NewChatPage));
            Routing.RegisterRoute("newgroup", typeof(Pages.NewGroupPage));
            Routing.RegisterRoute("call", typeof(Pages.CallPage));
            Routing.RegisterRoute("incomingcall", typeof(Pages.IncomingCallPage));
            auth.SignedOut += OnSignedOut;
            calls.IncomingCall += OnIncomingCall;
            calls.Finished += OnCallFinished;
            PushNotifications.ActionWaiting += async () => await ProcessPushAsync();
        }

        /// <summary>
        /// Carries out what the user tapped on a notification. Waits while nobody is signed in:
        /// the chat list calls this again once it appears.
        /// </summary>
        public async Task ProcessPushAsync()
        {
            if (_auth.Session is null)
                return;

            switch (PushNotifications.TakePending())
            {
                case PushAction.OpenChat open:
                    await OpenChatAsync(open.ChatId);
                    break;
                case PushAction.IncomingCall { Answer: false } call:
                    _calls.ShowIncomingFromPush(call.Invite, ring: true);
                    break;
                case PushAction.IncomingCall call:
                    _calls.ShowIncomingFromPush(call.Invite, ring: false);
                    try
                    {
                        await _calls.AcceptAsync();
                        await GoToAsync("call");
                    }
                    catch (CallException error)
                    {
                        await CurrentPage.DisplayAlertAsync("Звонок", error.Message, "ОК");
                    }
                    break;
            }
        }

        async Task OpenChatAsync(Guid chatId)
        {
            ChatDto? chat;
            try
            {
                chat = (await _api.GetChatsAsync()).FirstOrDefault(x => x.Id == chatId);
            }
            catch (ApiException)
            {
                return;
            }

            if (chat is null)
                return;
            if (CurrentPage is not MainPage)
                await GoToAsync("//main");
            if (MainPage.Current is { } main)
                await main.OpenChatAsync(chat);
        }

        void OnSignedOut() => MainThread.BeginInvokeOnMainThread(async () =>
        {
            await _hub.StopAsync();
            await GoToAsync("//login");
        });

        async void OnIncomingCall() => await GoToAsync("incomingcall");

        async void OnCallFinished(string? reason)
        {
            if (CurrentPage is Pages.CallPage or Pages.IncomingCallPage)
                await GoToAsync("..");
            if (reason is not null)
                await CurrentPage.DisplayAlertAsync("Звонок", reason, "ОК");
        }
    }
}
