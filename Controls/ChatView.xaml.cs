using System.Collections.ObjectModel;
using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Controls
{
    public enum MessageState { Sent, Sending, Failed }

    /// <summary><see cref="Id"/> is null until the server has accepted the message.</summary>
    public record MessageRow(long? Id, Guid ClientMessageId, string Text, string Sender, bool ShowSender, bool IsMine, DateTimeOffset SentAt, MessageState State)
    {
        /// <summary>The first message of a day gets the day above it.</summary>
        public bool ShowDay { get; init; } = true;
        /// <summary>Follows a message from the same person, so it sits closer and repeats no name.</summary>
        public bool Continues { get; init; }

        public string Day => Formatting.Day(SentAt);
        public bool ShowSenderName => ShowSender && !Continues;
        public Thickness Spacing => Continues ? new Thickness(0, 2, 0, 0) : new Thickness(0, 8, 0, 0);
        public string Status => State switch
        {
            MessageState.Sending => "Отправляется…",
            MessageState.Failed => "Не отправлено · нажмите, чтобы повторить",
            _ => Formatting.Time(SentAt),
        };
    }

    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Mine { get; set; }
        public DataTemplate? MineFailed { get; set; }
        public DataTemplate? Other { get; set; }

        protected override DataTemplate? OnSelectTemplate(object item, BindableObject container) => item switch
        {
            MessageRow { IsMine: false } => Other,
            MessageRow { State: MessageState.Failed } => MineFailed,
            _ => Mine,
        };
    }

    /// <summary>
    /// A conversation. Whoever hosts it calls <see cref="Activate"/> while it is on screen,
    /// so that it follows new messages, and <see cref="Deactivate"/> when it leaves.
    /// </summary>
    public partial class ChatView : ContentView
    {
        const int PageSize = 50;

        public static readonly BindableProperty ShowBackProperty =
            BindableProperty.Create(nameof(ShowBack), typeof(bool), typeof(ChatView), true,
                propertyChanged: (b, _, value) => ((ChatView)b).BackButton.IsVisible = (bool)value);

        readonly AuthService _auth;
        readonly ChatApi _api;
        readonly ChatHubClient _hub;
        readonly CallService _calls;
        readonly ObservableCollection<MessageRow> _rows = [];
        ChatDto? _chat;
        bool _active;

        public ChatView()
        {
            InitializeComponent();
            // Created from XAML, so the services come from the app rather than the constructor.
            var services = IPlatformApplication.Current!.Services;
            _auth = services.GetRequiredService<AuthService>();
            _api = services.GetRequiredService<ChatApi>();
            _hub = services.GetRequiredService<ChatHubClient>();
            _calls = services.GetRequiredService<CallService>();
            Messages.ItemsSource = _rows;
            MessageEntry.MaxLength = Limits.MessageMax;
        }

        public bool ShowBack
        {
            get => (bool)GetValue(ShowBackProperty);
            set => SetValue(ShowBackProperty, value);
        }

        public Guid? ChatId => _chat?.Id;

        public event Action? BackRequested;

        /// <summary>Opens a chat; showing the one already open keeps its messages and draft.</summary>
        public void Show(ChatDto chat)
        {
            if (_chat?.Id == chat.Id)
                return;

            _chat = chat;
            _rows.Clear();
            MessageEntry.Text = "";
            OlderButton.IsVisible = ErrorLabel.IsVisible = false;
            HeaderName.Text = chat.Title;
            HeaderAvatar.Name = chat.Title;
            HeaderAvatar.Seed = new ChatRow(chat).Seed;
            HeaderSubtitle.Text = chat.Kind == ChatKind.Group ? $"Участников: {chat.Members.Count}" : "";
            HeaderSubtitle.IsVisible = chat.Kind == ChatKind.Group;
            if (_active)
                _ = LoadLatestAsync();
        }

        public void Activate()
        {
            if (_active)
                return;

            _active = true;
            _hub.MessageReceived += OnMessageReceived;
            _hub.Connected += OnConnected;
            _ = LoadLatestAsync();
        }

        public void Deactivate()
        {
            if (!_active)
                return;

            _active = false;
            _hub.MessageReceived -= OnMessageReceived;
            _hub.Connected -= OnConnected;
        }

        async Task LoadLatestAsync()
        {
            if (_chat is not { } chat)
                return;

            try
            {
                var page = await _api.GetMessagesAsync(chat.Id);
                // Another chat may have opened while this one was loading.
                if (_chat?.Id != chat.Id)
                    return;

                foreach (var message in page)
                    Merge(message);
                if (_rows.Count == page.Count)
                    OlderButton.IsVisible = page.Count == PageSize;
                ErrorLabel.IsVisible = false;
                ScrollToEnd();
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
        }

        void OnBackClicked(object? sender, EventArgs e) => BackRequested?.Invoke();

        async void OnAudioCallClicked(object? sender, EventArgs e) => await StartCallAsync(video: false);

        async void OnVideoCallClicked(object? sender, EventArgs e) => await StartCallAsync(video: true);

        async Task StartCallAsync(bool video)
        {
            if (_chat is null)
                return;

            try
            {
                await _calls.StartAsync(_chat, video);
                await Shell.Current.GoToAsync("call");
            }
            catch (CallException error)
            {
                ShowError(error.Message);
            }
        }

        async void OnOlderClicked(object? sender, EventArgs e)
        {
            if (_chat is not { } chat || _rows.FirstOrDefault(row => row.Id is not null)?.Id is not { } oldest)
                return;

            try
            {
                var page = await _api.GetMessagesAsync(chat.Id, oldest);
                if (_chat?.Id != chat.Id)
                    return;

                foreach (var message in page)
                    Merge(message);
                OlderButton.IsVisible = page.Count == PageSize;
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
        }

        void OnConnected() => MainThread.BeginInvokeOnMainThread(async () => await LoadLatestAsync());

        void OnMessageReceived(MessageDto message) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (message.ChatId != _chat?.Id)
                return;

            Merge(message);
            ScrollToEnd();
        });

        async void OnSendClicked(object? sender, EventArgs e)
        {
            var text = MessageEntry.Text?.Trim() ?? "";
            if (text.Length == 0 || _chat is null)
                return;

            MessageEntry.Text = "";
            var row = new MessageRow(null, Guid.NewGuid(), text, _auth.Session?.User.DisplayName ?? "", false, true, DateTimeOffset.UtcNow, MessageState.Sending);
            _rows.Add(row);
            Relink(_rows.Count - 1);
            ScrollToEnd();
            await SendAsync(_chat, row);
        }

        async void OnMessageSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not MessageRow row)
                return;

            Messages.SelectedItem = null;
            if (row.State != MessageState.Failed || _chat is null)
                return;

            var sending = row with { State = MessageState.Sending };
            Replace(row, sending);
            await SendAsync(_chat, sending);
        }

        async Task SendAsync(ChatDto chat, MessageRow row)
        {
            try
            {
                // Resending with the same id can't duplicate a message that did get through.
                var sent = await _hub.SendMessageAsync(chat.Id, row.Text, row.ClientMessageId);
                if (_chat?.Id == chat.Id)
                    Merge(sent);
            }
            catch (Exception)
            {
                Replace(row, row with { State = MessageState.Failed });
            }
        }

        void Merge(MessageDto message)
        {
            var row = ToRow(message);
            var existing = IndexOf(message.ClientMessageId);
            if (existing >= 0)
            {
                _rows[existing] = row;
                Relink(existing);
                return;
            }

            // Unsent messages stay last, after everything the server has ordered.
            var index = 0;
            while (index < _rows.Count && _rows[index].Id is { } id && id < message.Id)
                index++;
            _rows.Insert(index, row);
            Relink(index);
        }

        // Rows are records and get replaced, so they are found by the id the client gave them.
        int IndexOf(Guid clientMessageId)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].ClientMessageId == clientMessageId)
                    return i;
            }
            return -1;
        }

        // A row's day header and grouping depend on the row before it, so the next row may change too.
        void Relink(int index)
        {
            for (var i = Math.Max(index, 0); i <= index + 1 && i < _rows.Count; i++)
            {
                var row = _rows[i];
                var previous = i > 0 ? _rows[i - 1] : null;
                var sameDay = previous is not null && previous.SentAt.ToLocalTime().Date == row.SentAt.ToLocalTime().Date;
                var linked = row with
                {
                    ShowDay = !sameDay,
                    Continues = sameDay && previous!.IsMine == row.IsMine && previous.Sender == row.Sender,
                };
                if (linked != row)
                    _rows[i] = linked;
            }
        }

        MessageRow ToRow(MessageDto message)
        {
            var mine = message.Sender.Id == _auth.Session?.User.Id;
            return new MessageRow(message.Id, message.ClientMessageId, message.Text, message.Sender.DisplayName,
                _chat?.Kind == ChatKind.Group && !mine, mine, message.SentAt, MessageState.Sent);
        }

        void Replace(MessageRow row, MessageRow replacement)
        {
            var index = IndexOf(row.ClientMessageId);
            if (index >= 0)
                _rows[index] = replacement with { ShowDay = _rows[index].ShowDay, Continues = _rows[index].Continues };
        }

        void ScrollToEnd()
        {
            if (_rows.Count > 0)
                Messages.ScrollTo(_rows.Count - 1, position: ScrollToPosition.End, animate: false);
        }

        void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.IsVisible = true;
        }
    }
}
