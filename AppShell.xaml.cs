using Mi_nU.Services;

namespace Mi_nU
{
    public partial class AppShell : Shell
    {
        readonly ChatHubClient _hub;

        public AppShell(AuthService auth, ChatHubClient hub, CallService calls)
        {
            InitializeComponent();
            _hub = hub;
            Routing.RegisterRoute("register", typeof(Pages.RegisterPage));
            Routing.RegisterRoute("chat", typeof(Pages.ChatPage));
            Routing.RegisterRoute("newchat", typeof(Pages.NewChatPage));
            Routing.RegisterRoute("newgroup", typeof(Pages.NewGroupPage));
            Routing.RegisterRoute("call", typeof(Pages.CallPage));
            Routing.RegisterRoute("incomingcall", typeof(Pages.IncomingCallPage));
            auth.SignedOut += OnSignedOut;
            calls.IncomingCall += OnIncomingCall;
            calls.Finished += OnCallFinished;
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
