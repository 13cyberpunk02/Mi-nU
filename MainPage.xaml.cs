using System.Collections.ObjectModel;
using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU
{
    /// <summary>
    /// <see cref="Viewer"/> is the signed-in user, whose own last message shows its ticks instead of a name.
    /// <see cref="Online"/>: the other person of a direct chat is in the app now.
    /// </summary>
    public record ChatRow(ChatDto Chat, Guid Viewer = default, bool Online = false)
    {
        public string Title => Chat.Title;
        public string Seed => Chat.Kind == ChatKind.Direct ? Chat.Title : Chat.Id.ToString();
        public string Preview => Chat.LastMessage switch
        {
            null => Chat.Kind == ChatKind.Group ? $"Участников: {Chat.Members.Count}" : "Сообщений пока нет",
            { Call: { } call } => Formatting.CallLine(call, LastIsMine),
            { Kind: MessageKind.System } last => last.Text,
            var last when LastIsMine && Chat.Kind == ChatKind.Group => $"Вы: {last.Text}",
            var last when Chat.Kind == ChatKind.Group => $"{last.Sender.DisplayName}: {last.Text}",
            var last => last.Text,
        };
        public string Time => Chat.LastMessage is { } last ? Formatting.Time(last.SentAt) : "";

        public bool HasUnread => Chat.UnreadCount > 0;
        public string Unread => Chat.UnreadCount > 99 ? "99+" : Chat.UnreadCount.ToString();
        public string UnreadDescription => $"Непрочитанных: {Chat.UnreadCount}";

        bool LastIsMine => Viewer != default && Chat.LastMessage?.Sender.Id == Viewer;
        bool LastRead => Chat.LastMessage is { } last && last.Id <= Chat.ReadUpTo;
        public bool ShowReceipt => LastIsMine && Chat.LastMessage?.Kind == MessageKind.Text;
        public string Receipt => LastRead ? Themes.Icons.CheckCheck : Themes.Icons.Check;
        public bool ReceiptRead => LastRead;
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

        /// <summary>A call as the viewer saw it: <paramref name="outgoing"/> when they were the caller.</summary>
        public static string CallTitle(CallSummary call, bool outgoing) => (call.DurationSeconds, outgoing, call.Video) switch
        {
            (null, true, false) => "Звонок без ответа",
            (null, true, true) => "Видеозвонок без ответа",
            (null, false, false) => "Пропущенный звонок",
            (null, false, true) => "Пропущенный видеозвонок",
            (_, true, false) => "Исходящий звонок",
            (_, true, true) => "Исходящий видеозвонок",
            (_, false, false) => "Входящий звонок",
            (_, false, true) => "Входящий видеозвонок",
        };

        /// <summary>The title with the length of an answered call, for the chat list.</summary>
        public static string CallLine(CallSummary call, bool outgoing) => call.DurationSeconds is { } seconds
            ? $"{CallTitle(call, outgoing)}, {Duration(seconds)}"
            : CallTitle(call, outgoing);

        public static string Duration(int seconds) => seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    public partial class MainPage : ContentPage
    {
        readonly AuthService _auth;
        readonly ChatApi _api;
        readonly ChatHubClient _hub;
        readonly PushNotifications _push;
        readonly DesktopNotifications _desktop;
        readonly AppActivity _activity;
        readonly PresenceService _presence;
        readonly ObservableCollection<ChatRow> _chats = [];
        bool _pushRegistered;

        public MainPage(AuthService auth, ChatApi api, ChatHubClient hub, PushNotifications push, DesktopNotifications desktop,
            AppActivity activity, PresenceService presence)
        {
            InitializeComponent();
            _auth = auth;
            _api = api;
            _hub = hub;
            _push = push;
            _desktop = desktop;
            _activity = activity;
            _presence = presence;
            // Kept up to date also while a chat page covers the list, so going back shows it right away.
            presence.Changed += OnPresenceChanged;
            hub.ChatUpdated += chat => MainThread.BeginInvokeOnMainThread(() => OnChatUpdated(chat));
            hub.ChatRemoved += chatId => MainThread.BeginInvokeOnMainThread(() => OnChatRemoved(chatId));
            ChatList.ItemsSource = _chats;
            SizeChanged += (_, _) => UpdateLayout();
            Current = this;
            // The next person to sign in registers this device for their own notifications.
            auth.SignedOut += () => _pushRegistered = false;
        }

        /// <summary>The chat list Shell created; pages that start a chat open it through here.</summary>
        public static MainPage? Current { get; private set; }

        /// <summary>The list's copy of the chat, kept current while other pages cover it.</summary>
        public ChatDto? Latest(Guid chatId) => _chats.FirstOrDefault(row => row.Chat.Id == chatId)?.Chat;

        /// <summary>Takes a group's new title, members and owner, from the server's event or a request's answer.</summary>
        public void ApplyUpdate(ChatDto update) => OnChatUpdated(update);

        void OnChatUpdated(ChatDto update)
        {
            var index = _chats.ToList().FindIndex(row => row.Chat.Id == update.Id);
            if (index < 0)
                return;

            // The unread count and last message stay this user's own.
            var chat = _chats[index].Chat with { Title = update.Title, Members = update.Members, OwnerId = update.OwnerId };
            _chats[index] = Row(chat);
            _desktop.Remember([chat]);
            if (Detail.ChatId == chat.Id)
                Detail.Update(chat);
            ApplySearch();
        }

        void OnChatRemoved(Guid chatId)
        {
            if (_chats.FirstOrDefault(row => row.Chat.Id == chatId) is { } row)
                _chats.Remove(row);
            if (Detail.ChatId == chatId)
            {
                Detail.Close();
                Detail.IsVisible = false;
                NoChatHint.IsVisible = true;
            }
            ApplySearch();
        }

        /// <summary>Wide enough to show the open chat beside the list.</summary>
        const double TwoPaneWidth = 900;
        const double ListWidth = 340;

        bool _twoPane;
        bool _visible;

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            _visible = true;
            _hub.MessageReceived += OnMessageReceived;
            _hub.ChatCreated += OnChatCreated;
            _hub.ChatRead += OnChatRead;
            _hub.Connected += OnConnected;
            _hub.Start();
            if (_twoPane && Detail.ChatId is not null)
                Detail.Activate();
            if (!_pushRegistered)
            {
                _pushRegistered = true;
                _ = _push.RegisterAsync();
            }
            await ReloadAsync();
            // A notification tapped while the app was closed or signed out waited for this.
            if (Shell.Current is AppShell shell)
                await shell.ProcessPushAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _visible = false;
            _hub.MessageReceived -= OnMessageReceived;
            _hub.ChatCreated -= OnChatCreated;
            _hub.ChatRead -= OnChatRead;
            _hub.Connected -= OnConnected;
            Detail.Deactivate();
        }

        void UpdateLayout()
        {
            var twoPane = Width >= TwoPaneWidth;
            if (twoPane == _twoPane)
                return;

            _twoPane = twoPane;
            Root.ColumnDefinitions[0].Width = twoPane ? new GridLength(ListWidth) : GridLength.Star;
            Root.ColumnDefinitions[1].Width = twoPane ? GridLength.Star : new GridLength(0);
            DetailPane.IsVisible = twoPane;
            if (twoPane && _visible && Detail.ChatId is not null)
                Detail.Activate();
            else if (!twoPane)
                Detail.Deactivate();
            SelectOpenChat();
        }

        // In two panes the open chat stays highlighted in the list, also after the list is rebuilt.
        void SelectOpenChat()
        {
            var open = _twoPane ? (ChatList.ItemsSource as IEnumerable<ChatRow>)?.FirstOrDefault(row => row.Chat.Id == Detail.ChatId) : null;
            if (!Equals(ChatList.SelectedItem, open))
                ChatList.SelectedItem = open;
        }

        void OpenInPane(ChatDto chat)
        {
            Detail.Show(chat);
            Detail.IsVisible = true;
            NoChatHint.IsVisible = false;
            if (_visible)
                Detail.Activate();
            SelectOpenChat();
        }

        async Task ReloadAsync()
        {
            try
            {
                var chats = await _api.GetChatsAsync();
                _desktop.Remember(chats);
                ErrorLabel.IsVisible = false;

                // Rebuilding an unchanged list would swallow a tap that lands in the middle of it,
                // so only a new order rebuilds it and changed counters replace just their rows.
                static (Guid, long?) Order(ChatDto chat) => (chat.Id, chat.LastMessage?.Id);
                static (int, long) Marks(ChatDto chat) => (chat.UnreadCount, chat.ReadUpTo);
                if (!chats.Select(Order).SequenceEqual(_chats.Select(row => Order(row.Chat))))
                {
                    _chats.Clear();
                    foreach (var chat in chats)
                        _chats.Add(Row(chat));
                }
                else
                {
                    for (var i = 0; i < chats.Count; i++)
                    {
                        if (Marks(chats[i]) != Marks(_chats[i].Chat))
                            _chats[i] = Row(chats[i]);
                    }
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
                _chats.Insert(0, Row(chat));
            ApplySearch();
        });

        ChatRow Row(ChatDto chat)
        {
            var me = _auth.Session?.User.Id ?? default;
            var online = chat.Kind == ChatKind.Direct && chat.Members.Any(member => member.Id != me && _presence.IsOnline(member.Id));
            return new(chat, me, online);
        }

        void OnPresenceChanged(Guid userId)
        {
            var changed = false;
            for (var i = 0; i < _chats.Count; i++)
            {
                if (_chats[i].Chat.Kind != ChatKind.Direct || _chats[i].Chat.Members.All(member => member.Id != userId))
                    continue;
                var row = Row(_chats[i].Chat);
                if (row.Online != _chats[i].Online)
                {
                    _chats[i] = row;
                    changed = true;
                }
            }
            if (changed)
                ApplySearch();
        }

        /// <summary>The chat is open beside the list and the user is looking at it, so its messages are read as they come.</summary>
        bool IsInView(Guid chatId) => _twoPane && _visible && _activity.IsActive && Detail.ChatId == chatId;

        void OnMessageReceived(MessageDto message) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            var row = _chats.FirstOrDefault(x => x.Chat.Id == message.ChatId);
            if (row is null)
            {
                await ReloadAsync();
                return;
            }

            var chat = row.Chat with { LastMessage = message };
            if (message.Sender.Id == _auth.Session?.User.Id)
                // Writing in a chat, from here or another device, means having read it.
                chat = chat with { UnreadCount = 0 };
            else
                // The sender has read everything before their message.
                chat = chat with
                {
                    UnreadCount = IsInView(chat.Id) ? 0 : chat.UnreadCount + 1,
                    ReadUpTo = Math.Max(chat.ReadUpTo, message.Id),
                };

            _chats.Remove(row);
            _chats.Insert(0, Row(chat));
            ApplySearch();
        });

        void OnChatRead(ReadReceipt receipt) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            var index = _chats.ToList().FindIndex(x => x.Chat.Id == receipt.ChatId);
            if (index < 0)
                return;

            var chat = _chats[index].Chat;
            if (receipt.UserId != _auth.Session?.User.Id)
                chat = chat with { ReadUpTo = Math.Max(chat.ReadUpTo, receipt.MessageId) };
            else if (receipt.MessageId >= (chat.LastMessage?.Id ?? 0))
                chat = chat with { UnreadCount = 0 };
            else
            {
                // Read only part of the chat: the server knows how much is left.
                await ReloadAsync();
                return;
            }

            if (chat != _chats[index].Chat)
            {
                _chats[index] = Row(chat);
                ApplySearch();
            }
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
                SelectOpenChat();
                return;
            }

            ChatList.ItemsSource = _chats.Where(row => row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            EmptyTitle.Text = "Ничего не найдено";
            EmptyHint.Text = "Новых собеседников ищите через «+»";
            SelectOpenChat();
        }

        async void OnChatSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not ChatRow row)
                return;

            if (_twoPane)
            {
                OpenInPane(row.Chat);
                return;
            }

            ChatList.SelectedItem = null;
            await Shell.Current.GoToAsync("chat", new ShellNavigationQueryParameters { ["chat"] = row.Chat });
        }

        /// <summary>Opens a chat the way this window shows them: beside the list, or as its own page.</summary>
        public async Task OpenChatAsync(ChatDto chat)
        {
            if (_twoPane)
                OpenInPane(chat);
            else
                await Shell.Current.GoToAsync("chat", new ShellNavigationQueryParameters { ["chat"] = chat });
        }

        async void OnNewChatClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("newchat");

        async void OnProfileClicked(object? sender, EventArgs e)
        {
            const string logout = "Выйти";
            var name = _auth.Session?.User.DisplayName ?? "";
            if (await DisplayActionSheetAsync(name, "Отмена", logout) != logout)
                return;

            // While still signed in: the server must know whose notifications to stop.
            await _push.UnregisterAsync();
            // AppShell returns to the sign-in screen when the session ends.
            await _auth.LogoutAsync();
        }
    }
}
