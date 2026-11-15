using Microsoft.Extensions.DependencyInjection;
using Mi_nU.Services;

namespace Mi_nU
{
    public partial class App : Application
    {
        readonly AppShell _shell;
        readonly AppActivity _activity;
        readonly DesktopNotifications _notifications;

        public App(AppShell shell, AppActivity activity, DesktopNotifications notifications)
        {
            InitializeComponent();
            Themes.ThemeManager.Apply(this);
            RequestedThemeChanged += (_, _) => Themes.ThemeManager.Apply(this);
            _shell = shell;
            _activity = activity;
            _notifications = notifications;
            notifications.Start();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(_shell);
            window.Activated += (_, _) => _activity.Set(true);
            window.Deactivated += (_, _) => _activity.Set(false);
            window.Destroying += (_, _) => _notifications.Stop();
            return window;
        }
    }
}
