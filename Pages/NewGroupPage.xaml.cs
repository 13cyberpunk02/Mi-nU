using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Pages
{
    public record GroupUserRow(UserDto User, bool Selected)
    {
        public string Name => (Selected ? "✓ " : "") + User.DisplayName;
        public string UserName => User.UserName;
    }

    public partial class NewGroupPage : ContentPage
    {
        readonly ChatApi _api;
        readonly UserSearch _search;
        readonly Dictionary<Guid, UserDto> _selected = [];
        List<UserDto> _found = [];

        public NewGroupPage(ChatApi api)
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

                _found = users;
                ShowResults();
                ErrorLabel.IsVisible = false;
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
        }

        void OnUserSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not GroupUserRow row)
                return;

            if (!_selected.Remove(row.User.Id))
                _selected.Add(row.User.Id, row.User);

            Results.SelectedItem = null;
            ShowResults();
            MembersLabel.Text = _selected.Count == 0
                ? "Участники не выбраны"
                : "Участники: " + string.Join(", ", _selected.Values.Select(user => user.DisplayName));
        }

        async void OnCreateClicked(object? sender, EventArgs e)
        {
            var title = TitleEntry.Text?.Trim() ?? "";
            if (title.Length is 0 or > Limits.ChatTitleMax)
            {
                ShowError($"Название группы: от 1 до {Limits.ChatTitleMax} символов.");
                return;
            }
            if (_selected.Count == 0)
            {
                ShowError("Выберите хотя бы одного участника.");
                return;
            }

            CreateButton.IsEnabled = false;
            try
            {
                var chat = await _api.CreateGroupChatAsync(title, [.. _selected.Keys]);
                await Shell.Current.GoToAsync("../chat", new ShellNavigationQueryParameters { ["chat"] = chat });
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
            finally
            {
                CreateButton.IsEnabled = true;
            }
        }

        void ShowResults() =>
            Results.ItemsSource = _found.Select(user => new GroupUserRow(user, _selected.ContainsKey(user.Id))).ToList();

        void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.IsVisible = true;
        }
    }
}
