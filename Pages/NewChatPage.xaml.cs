using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Pages
{
    public partial class NewChatPage : ContentPage
    {
        readonly ChatApi _api;
        readonly UserSearch _search;

        public NewChatPage(ChatApi api)
        {
            InitializeComponent();
            _api = api;
            _search = new UserSearch(api);
        }

        async void OnSearchChanged(object? sender, TextChangedEventArgs e)
        {
            try
            {
                var users = await _search.SearchAsync(e.NewTextValue ?? "");
                if (users is null)
                    return;

                Results.ItemsSource = users;
                HintLabel.Text = (e.NewTextValue?.Trim().Length ?? 0) < 2
                    ? "Введите хотя бы 2 буквы логина или имени"
                    : "Никого не нашлось";
                ErrorLabel.IsVisible = false;
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
        }

        async void OnUserSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not UserDto user)
                return;

            Results.SelectedItem = null;
            try
            {
                var chat = await _api.CreateDirectChatAsync(user.Id);
                await Shell.Current.GoToAsync("..");
                if (MainPage.Current is { } main)
                    await main.OpenChatAsync(chat);
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
        }

        async void OnNewGroupTapped(object? sender, EventArgs e) => await Shell.Current.GoToAsync("../newgroup");

        void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.IsVisible = true;
        }
    }
}
