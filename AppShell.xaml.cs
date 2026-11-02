using Mi_nU.Services;

namespace Mi_nU
{
    public partial class AppShell : Shell
    {
        readonly ChatHubClient _hub;

        public AppShell(AuthService auth, ChatHubClient hub)
        {
            InitializeComponent();
            _hub = hub;
            Routing.RegisterRoute("register", typeof(Pages.RegisterPage));
            Routing.RegisterRoute("chat", typeof(Pages.ChatPage));
            Routing.RegisterRoute("newchat", typeof(Pages.NewChatPage));
            Routing.RegisterRoute("newgroup", typeof(Pages.NewGroupPage));
            auth.SignedOut += OnSignedOut;
        }

        void OnSignedOut() => MainThread.BeginInvokeOnMainThread(async () =>
        {
            await _hub.StopAsync();
            await GoToAsync("//login");
        });
    }
}
