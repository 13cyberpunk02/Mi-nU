using System.Collections.ObjectModel;
using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Pages
{
    public enum MessageState { Sent, Sending, Failed }

    /// <summary><see cref="Id"/> is null until the server has accepted the message.</summary>
    public record MessageRow(long? Id, Guid ClientMessageId, string Text, string Sender, bool ShowSender, bool IsMine, DateTimeOffset SentAt, MessageState State)
    {
        public string Status => State switch
        {
            MessageState.Sending => "Отправляется…",
            MessageState.Failed => "Не отправлено. Нажмите, чтобы повторить",
            _ => Formatting.Time(SentAt),
        };
    }

    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Mine { get; set; }
        public DataTemplate? Other { get; set; }

        protected override DataTemplate? OnSelectTemplate(object item, BindableObject container) =>
            ((MessageRow)item).IsMine ? Mine : Other;
    }

    public partial class ChatPage : ContentPage, IQueryAttributable
    {
        const int PageSize = 50;

        readonly AuthService _auth;
        readonly ChatApi _api;
        readonly ChatHubClient _hub;
        readonly ObservableCollection<MessageRow> _rows = [];
        ChatDto? _chat;

        public ChatPage(AuthService auth, ChatApi api, ChatHubClient hub)
        {
            InitializeComponent();
            _auth = auth;
            _api = api;
            _hub = hub;
            Messages.ItemsSource = _rows;
            MessageEntry.MaxLength = Limits.MessageMax;
        }

        public void ApplyQueryAttributes(IDictionary<string, object> query)
        {
            _chat = (ChatDto)query["chat"];
            Title = _chat.Title;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            _hub.MessageReceived += OnMessageReceived;
            _hub.Connected += OnConnected;
            await LoadLatestAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _hub.MessageReceived -= OnMessageReceived;
            _hub.Connected -= OnConnected;
        }

        async Task LoadLatestAsync()
        {
            if (_chat is null)
                return;

            try
            {
                var page = await _api.GetMessagesAsync(_chat.Id);
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

        async void OnOlderClicked(object? sender, EventArgs e)
        {
            if (_chat is null || _rows.FirstOrDefault(row => row.Id is not null)?.Id is not { } oldest)
                return;

            try
            {
                var page = await _api.GetMessagesAsync(_chat.Id, oldest);
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

        void OnMessageReceived(MessageDto message)
        {
            if (message.ChatId != _chat?.Id)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Merge(message);
                ScrollToEnd();
            });
        }

        async void OnSendClicked(object? sender, EventArgs e)
        {
            var text = MessageEntry.Text?.Trim() ?? "";
            if (text.Length == 0 || _chat is null)
                return;

            MessageEntry.Text = "";
            var row = new MessageRow(null, Guid.NewGuid(), text, "", false, true, DateTimeOffset.UtcNow, MessageState.Sending);
            _rows.Add(row);
            ScrollToEnd();
            await SendAsync(row);
        }

        async void OnMessageSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not MessageRow row)
                return;

            Messages.SelectedItem = null;
            if (row.State != MessageState.Failed)
                return;

            var sending = row with { State = MessageState.Sending };
            Replace(row, sending);
            await SendAsync(sending);
        }

        async Task SendAsync(MessageRow row)
        {
            try
            {
                // Resending with the same id can't duplicate a message that did get through.
                Merge(await _hub.SendMessageAsync(_chat!.Id, row.Text, row.ClientMessageId));
            }
            catch (Exception)
            {
                if (_rows.Contains(row))
                    Replace(row, row with { State = MessageState.Failed });
            }
        }

        void Merge(MessageDto message)
        {
            var row = ToRow(message);
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].ClientMessageId == message.ClientMessageId)
                {
                    _rows[i] = row;
                    return;
                }
            }

            // Unsent messages stay last, after everything the server has ordered.
            var index = 0;
            while (index < _rows.Count && _rows[index].Id is { } id && id < message.Id)
                index++;
            _rows.Insert(index, row);
        }

        MessageRow ToRow(MessageDto message)
        {
            var mine = message.Sender.Id == _auth.Session?.User.Id;
            return new MessageRow(message.Id, message.ClientMessageId, message.Text, message.Sender.DisplayName,
                _chat?.Kind == ChatKind.Group && !mine, mine, message.SentAt, MessageState.Sent);
        }

        void Replace(MessageRow row, MessageRow replacement)
        {
            var index = _rows.IndexOf(row);
            if (index >= 0)
                _rows[index] = replacement;
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
