using Mi_nU.Services;

namespace Mi_nU.Pages
{
    public partial class LoginPage : ContentPage
    {
        readonly AuthService _auth;

        public LoginPage(AuthService auth)
        {
            InitializeComponent();
            _auth = auth;
#if DEBUG
            ServerSection.IsVisible = true;
            ServerEntry.Text = auth.ServerUrl;
#endif
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            PasswordEntry.Text = "";
            ErrorLabel.IsVisible = false;
        }

        void OnUserNameCompleted(object? sender, EventArgs e) => PasswordEntry.Focus();

        async void OnLoginClicked(object? sender, EventArgs e)
        {
            var userName = UserNameEntry.Text?.Trim() ?? "";
            var password = PasswordEntry.Text ?? "";
            if (userName.Length == 0 || password.Length == 0)
            {
                ShowError("Введите логин и пароль.");
                return;
            }
            if (Busy.IsRunning)
                return;

            SetBusy(true);
            try
            {
#if DEBUG
                _auth.ServerUrl = ServerEntry.Text ?? "";
#endif
                await _auth.LoginAsync(userName, password);
                await Shell.Current.GoToAsync("//main");
            }
            catch (AuthException error)
            {
                ShowError(error.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        async void OnRegisterClicked(object? sender, EventArgs e)
        {
#if DEBUG
            _auth.ServerUrl = ServerEntry.Text ?? "";
#endif
            await Shell.Current.GoToAsync("register");
        }

        void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.IsVisible = true;
        }

        void SetBusy(bool busy)
        {
            if (busy)
                ErrorLabel.IsVisible = false;
            Busy.IsRunning = Busy.IsVisible = busy;
            LoginButton.IsEnabled = RegisterButton.IsEnabled = !busy;
        }
    }
}
