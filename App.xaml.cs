using Microsoft.Extensions.DependencyInjection;
using Mi_nU.Services;

namespace Mi_nU
{
    public partial class App : Application
    {
        readonly AppShell _shell;
        readonly DesktopNotifications _notifications;

        public App(AppShell shell, DesktopNotifications notifications)
        {
            InitializeComponent();
            Themes.ThemeManager.Apply(this);
            RequestedThemeChanged += (_, _) => Themes.ThemeManager.Apply(this);
            _shell = shell;
            _notifications = notifications;
            notifications.Start();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(_shell);
            // Notifications only matter while the window is behind something else or minimized.
            window.Activated += (_, _) => _notifications.WindowActive = true;
            window.Deactivated += (_, _) => _notifications.WindowActive = false;
            window.Destroying += (_, _) => _notifications.Stop();
            return window;
        }
    }
}
