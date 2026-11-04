using System.Collections.ObjectModel;
using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU
{
    public record ChatRow(ChatDto Chat)
    {
        public string Title => Chat.Title;
        public string Seed => Chat.Kind == ChatKind.Direct ? Chat.Title : Chat.Id.ToString();
        public string Preview => Chat.LastMessage switch
        {
            null => Chat.Kind == ChatKind.Group ? $"Участников: {Chat.Members.Count}" : "Сообщений пока нет",
            var last when Chat.Kind == ChatKind.Group => $"{last.Sender.DisplayName}: {last.Text}",
            var last => last.Text,
        };
        public string Time => Chat.LastMessage is { } last ? Formatting.Time(last.SentAt) : "";
    }

    public static class Formatting
    {
        public static string Time(DateTimeOffset moment)
        {
            var local = moment.ToLocalTime();
            return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("dd.MM HH:mm");
        }

        public static string Day(DateTimeOffset moment)
        {
            var date = moment.ToLocalTime().Date;
            if (date == DateTime.Today)
                return "Сегодня";
            if (date == DateTime.Today.AddDays(-1))
                return "Вчера";
            return date.ToString(date.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy");
        }
    }

    public partial class MainPage : ContentPage
    {
        readonly AuthService _auth;
        readonly ChatApi _api;
        readonly ChatHubClient _hub;
        readonly ObservableCollection<ChatRow> _chats = [];

        public MainPage(AuthService auth, ChatApi api, ChatHubClient hub)
        {
            InitializeComponent();
            _auth = auth;
            _api = api;
            _hub = hub;
            ChatList.ItemsSource = _chats;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            _hub.MessageReceived += OnMessageReceived;
            _hub.ChatCreated += OnChatCreated;
            _hub.Connected += OnConnected;
            _hub.Start();
            await ReloadAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _hub.MessageReceived -= OnMessageReceived;
            _hub.ChatCreated -= OnChatCreated;
            _hub.Connected -= OnConnected;
        }

        async Task ReloadAsync()
        {
            try
            {
                var chats = await _api.GetChatsAsync();
                ErrorLabel.IsVisible = false;

                // Rebuilding an unchanged list would swallow a tap that lands in the middle of it.
                static (Guid, long?) Key(ChatDto chat) => (chat.Id, chat.LastMessage?.Id);
                if (!chats.Select(Key).SequenceEqual(_chats.Select(row => Key(row.Chat))))
                {
                    _chats.Clear();
                    foreach (var chat in chats)
                        _chats.Add(new ChatRow(chat));
                }
                ApplySearch();
            }
            catch (ApiException error)
            {
                ErrorLabel.Text = error.Message;
                // The session is gone when signed out, and the sign-in screen is already on its way.
                ErrorLabel.IsVisible = _auth.Session is not null;
            }
        }

        void OnConnected() => MainThread.BeginInvokeOnMainThread(async () => await ReloadAsync());

        void OnChatCreated(ChatDto chat) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_chats.All(row => row.Chat.Id != chat.Id))
                _chats.Insert(0, new ChatRow(chat));
            ApplySearch();
        });

        void OnMessageReceived(MessageDto message) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            var row = _chats.FirstOrDefault(x => x.Chat.Id == message.ChatId);
            if (row is null)
            {
                await ReloadAsync();
                return;
            }

            _chats.Remove(row);
            _chats.Insert(0, new ChatRow(row.Chat with { LastMessage = message }));
            ApplySearch();
        });

        void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

        // The live list stays bound while nothing is typed, so updates never rebuild it under the user's finger.
        void ApplySearch()
        {
            var query = SearchEntry.Text?.Trim() ?? "";
            if (query.Length == 0)
            {
                if (ChatList.ItemsSource != _chats)
                    ChatList.ItemsSource = _chats;
                EmptyTitle.Text = "Чатов пока нет";
                EmptyHint.Text = "Нажмите «+», чтобы найти собеседника или создать группу";
                return;
            }

            ChatList.ItemsSource = _chats.Where(row => row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            EmptyTitle.Text = "Ничего не найдено";
            EmptyHint.Text = "Новых собеседников ищите через «+»";
        }

        async void OnChatSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not ChatRow row)
                return;

            ChatList.SelectedItem = null;
            await Shell.Current.GoToAsync("chat", new ShellNavigationQueryParameters { ["chat"] = row.Chat });
        }

        async void OnNewChatClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("newchat");

        async void OnProfileClicked(object? sender, EventArgs e)
        {
            const string logout = "Выйти";
            var name = _auth.Session?.User.DisplayName ?? "";
            // AppShell returns to the sign-in screen when the session ends.
            if (await DisplayActionSheetAsync(name, "Отмена", logout) == logout)
                _auth.Logout();
        }
    }
}
