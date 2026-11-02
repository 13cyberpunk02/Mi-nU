using System.Text.RegularExpressions;
using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Pages
{
    public partial class RegisterPage : ContentPage
    {
        readonly AuthService _auth;

        public RegisterPage(AuthService auth)
        {
            InitializeComponent();
            _auth = auth;
        }

        void OnFieldCompleted(object? sender, EventArgs e)
        {
            if (sender == UserNameEntry)
                DisplayNameEntry.Focus();
            else if (sender == DisplayNameEntry)
                PasswordEntry.Focus();
            else
                ConfirmEntry.Focus();
        }

        async void OnRegisterClicked(object? sender, EventArgs e)
        {
            var userName = UserNameEntry.Text?.Trim() ?? "";
            var displayName = DisplayNameEntry.Text?.Trim() ?? "";
            var password = PasswordEntry.Text ?? "";

            var problem = Validate(userName, displayName, password, ConfirmEntry.Text ?? "");
            if (problem is not null)
            {
                ShowError(problem);
                return;
            }
            if (Busy.IsRunning)
                return;

            SetBusy(true);
            try
            {
                await _auth.RegisterAsync(userName, displayName, password);
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

        static string? Validate(string userName, string displayName, string password, string confirm)
        {
            if (userName.Length is < Limits.UserNameMin or > Limits.UserNameMax || !Regex.IsMatch(userName, "^[A-Za-z0-9_]+$"))
                return $"Логин: латинские буквы, цифры и _, от {Limits.UserNameMin} до {Limits.UserNameMax} символов.";
            if (displayName.Length is 0 or > Limits.DisplayNameMax)
                return $"Имя: от 1 до {Limits.DisplayNameMax} символов.";
            if (password.Length < Limits.PasswordMin)
                return $"Пароль должен быть не короче {Limits.PasswordMin} символов.";
            if (password != confirm)
                return "Пароли не совпадают.";
            return null;
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
            RegisterButton.IsEnabled = !busy;
        }
    }
}
