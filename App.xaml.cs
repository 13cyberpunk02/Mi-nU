using Microsoft.Extensions.DependencyInjection;

namespace Mi_nU
{
    public partial class App : Application
    {
        readonly AppShell _shell;

        public App(AppShell shell)
        {
            InitializeComponent();
            _shell = shell;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(_shell);
        }
    }
}