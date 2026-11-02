using Mi_nU.Services;

namespace Mi_nU.Pages
{
    public partial class StartupPage : ContentPage
    {
        readonly AuthService _auth;

        public StartupPage(AuthService auth)
        {
            InitializeComponent();
            _auth = auth;
            // Not OnAppearing: on Windows that fires while the window is still being built, and navigating then crashes the app.
            Loaded += OnLoaded;
        }

        async void OnLoaded(object? sender, EventArgs e)
        {
            Loaded -= OnLoaded;
            await Shell.Current.GoToAsync(await _auth.RestoreAsync() ? "//main" : "//login");
        }
    }
}
