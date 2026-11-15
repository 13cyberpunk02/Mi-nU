using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Pages
{
    public record GroupUserRow(UserDto User, bool Selected)
    {
        public string Name => User.DisplayName;
        public string UserName => User.UserName;
        public bool NotSelected => !Selected;
    }

    /// <summary>Creates a group, or with an "addTo" chat in the query, adds people to that group.</summary>
    public partial class NewGroupPage : ContentPage, IQueryAttributable
    {
        readonly ChatApi _api;
        readonly UserSearch _search;
        readonly Dictionary<Guid, UserDto> _selected = [];
        List<UserDto> _found = [];
        ChatDto? _addTo;

        public NewGroupPage(ChatApi api)
        {
            InitializeComponent();
            _api = api;
            _search = new UserSearch(api);
        }

        public void ApplyQueryAttributes(IDictionary<string, object> query)
        {
            if (query.TryGetValue("addTo", out var value) && value is ChatDto chat)
            {
                _addTo = chat;
                Title = "Добавить участников";
                TitleSection.IsVisible = false;
                CreateButton.Text = "Добавить";
            }
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
            if (_addTo is { } group)
            {
                await AddToGroupAsync(group);
                return;
            }

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
                await Shell.Current.GoToAsync("..");
                if (MainPage.Current is { } main)
                    await main.OpenChatAsync(chat);
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

        async Task AddToGroupAsync(ChatDto group)
        {
            if (_selected.Count == 0)
            {
                ShowError("Выберите, кого добавить.");
                return;
            }

            CreateButton.IsEnabled = false;
            try
            {
                var updated = await _api.AddMembersAsync(group.Id, [.. _selected.Keys]);
                // Straight into the list, which the group page reads on its way back; the server's event may come later.
                MainPage.Current?.ApplyUpdate(updated);
                await Shell.Current.GoToAsync("..");
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

        // People already in the group aren't offered again.
        void ShowResults() =>
            Results.ItemsSource = _found
                .Where(user => _addTo is null || _addTo.Members.All(member => member.Id != user.Id))
                .Select(user => new GroupUserRow(user, _selected.ContainsKey(user.Id))).ToList();

        void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.IsVisible = true;
        }
    }
}
