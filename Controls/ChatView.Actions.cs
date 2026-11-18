using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Controls
{
    /// <summary>A message found by searching the chat, as a line in the results.</summary>
    public sealed record SearchHit(long Id, string Sender, string When, string Text)
    {
        public string Description => $"{Sender}: {Text}, {When}";
    }

    /// <summary>
    /// What can be done with a message once it is sent: answering it, reacting, copying, forwarding, and for one's own,
    /// editing and deleting. Also who is typing, and searching the chat.
    /// </summary>
    public partial class ChatView
    {
        /// <summary>The message being answered, shown over the field until sent or dropped.</summary>
        MessageRow? _replyTo;
        /// <summary>The user's own message being changed; the field holds its text meanwhile.</summary>
        MessageRow? _editing;

        // ---- The menu

        async Task ShowMenuAsync(MessageRow row)
        {
            const string reply = "Ответить";
            const string react = "Реакция";
            const string copy = "Копировать";
            const string forward = "Переслать";
            const string edit = "Изменить";
            const string delete = "Удалить";
            var choices = new List<string> { reply, react };
            if (row.HasText)
                choices.Add(copy);
            choices.Add(forward);
            // A voice or video message has no text to change; a photo's caption does.
            var recorded = row.Attachments is { Count: 1 } files && (files[0].File.IsVoice || files[0].File.IsVideoNote);
            if (row.IsMine && !recorded)
                choices.Add(edit);

            var choice = await Shell.Current.DisplayActionSheetAsync(null, "Отмена", row.IsMine ? delete : null, [.. choices]);
            switch (choice)
            {
                case reply:
                    StartReply(row);
                    break;
                case react:
                    await ChooseReactionAsync(row);
                    break;
                case copy:
                    await Clipboard.Default.SetTextAsync(row.Text);
                    break;
                case forward:
                    await ForwardAsync(row);
                    break;
                case edit:
                    StartEdit(row);
                    break;
                case delete:
                    await DeleteAsync(row);
                    break;
            }
        }

        // ---- Replying and editing: a bar over the field says which

        void StartReply(MessageRow row)
        {
            ClearContext();
            _replyTo = row;
            ShowContext(Themes.Icons.Reply, $"Ответ {row.Sender}", SummaryOf(row));
            MessageEntry.Focus();
        }

        void StartEdit(MessageRow row)
        {
            ClearContext();
            _editing = row;
            ShowContext(Themes.Icons.Pencil, "Изменение сообщения", SummaryOf(row));
            MessageEntry.Text = row.Text;
            MessageEntry.Focus();
            UpdateSendButtons();
        }

        void ShowContext(string icon, string title, string text)
        {
            ContextIcon.Text = icon;
            ContextTitle.Text = title;
            ContextText.Text = text;
            ContextBar.IsVisible = true;
        }

        void ClearContext()
        {
            var wasEditing = _editing is not null;
            _replyTo = null;
            _editing = null;
            ContextBar.IsVisible = false;
            // The text being edited isn't a draft of a new message.
            if (wasEditing)
                MessageEntry.Text = "";
            UpdateSendButtons();
        }

        void OnContextCancelClicked(object? sender, EventArgs e) => ClearContext();

        void UpdateSendButtons()
        {
            SendButton.IsVisible = !string.IsNullOrWhiteSpace(MessageEntry.Text) || _editing is not null;
            RecordButton.IsVisible = VideoNoteButton.IsVisible = !SendButton.IsVisible;
        }

        /// <summary>The message being answered, as the new message quotes it; the bar goes as it is sent.</summary>
        ReplyPreview? TakeReply()
        {
            if (_replyTo is not { Id: { } id } row)
                return null;
            ClearContext();
            return new ReplyPreview(id, row.Sender, SummaryOf(row));
        }

        async Task SaveEditAsync(string text)
        {
            if (_editing is not { Id: { } id } row)
                return;
            if (text.Length == 0 && !row.HasAttachments)
            {
                ShowError("Сообщение не может быть пустым. Чтобы убрать его, удалите его.");
                return;
            }
            try
            {
                Apply(await _hub.EditMessageAsync(id, text));
                ClearContext();
                ErrorLabel.IsVisible = false;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ShowError("Не удалось изменить сообщение. Проверьте соединение.");
            }
        }

        // ---- Deleting, reacting, forwarding

        async Task DeleteAsync(MessageRow row)
        {
            if (row.Id is not { } id
                || !await Shell.Current.DisplayAlertAsync("Удалить сообщение?", "Оно исчезнет у всех в этом чате.", "Удалить", "Отмена"))
                return;
            try
            {
                await _hub.DeleteMessageAsync(id);
                Remove(id);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ShowError("Не удалось удалить сообщение. Проверьте соединение.");
            }
        }

        async Task ChooseReactionAsync(MessageRow row)
        {
            const string none = "Убрать реакцию";
            var choice = await Shell.Current.DisplayActionSheetAsync("Реакция", "Отмена", row.MyReaction is null ? null : none, [.. Reactions.Allowed]);
            if (choice == none)
                await ReactAsync(row, null);
            else if (choice is not null && Reactions.Allowed.Contains(choice))
                await ReactAsync(row, choice);
        }

        async Task ReactAsync(MessageRow row, string? emoji)
        {
            if (row.Id is not { } id)
                return;
            try
            {
                Apply(await _hub.ReactAsync(id, emoji));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ShowError("Не удалось поставить реакцию. Проверьте соединение.");
            }
        }

        /// <summary>A chip under a message: the user's own reaction comes off, another emoji becomes theirs.</summary>
        async void OnChipTapped(ReactionChip chip)
        {
            if (_rows.FirstOrDefault(row => row.Id == chip.MessageId) is { } row)
                await ReactAsync(row, chip.Mine ? null : chip.Emoji);
        }

        async Task ForwardAsync(MessageRow row)
        {
            if (row.Id is not { } id || MainPage.Current?.Chats is not { Count: > 0 } chats)
                return;
            // Titles can repeat (two people with one name); the list order tells them apart.
            var titles = chats.Select((chat, i) => chats.Take(i).Any(other => other.Title == chat.Title) ? $"{chat.Title} ({i + 1})" : chat.Title).ToList();
            var choice = await Shell.Current.DisplayActionSheetAsync("Переслать в", "Отмена", null, [.. titles]);
            var index = choice is null ? -1 : titles.IndexOf(choice);
            if (index < 0)
                return;
            try
            {
                await _hub.ForwardAsync(id, chats[index].Id);
                ErrorLabel.IsVisible = false;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ShowError("Не удалось переслать сообщение. Проверьте соединение.");
            }
        }

        // ---- Changes from the server: edits, reactions and deletions, by anyone

        void OnMessageUpdated(MessageDto message) => MainThread.BeginInvokeOnMainThread(() => Apply(message));

        void OnMessageDeleted(Guid chatId, long messageId) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (chatId == _chat?.Id)
                Remove(messageId);
        });

        /// <summary>The message as it now is, in its place; replies to it quote the new text.</summary>
        void Apply(MessageDto message)
        {
            if (message.ChatId != _chat?.Id)
                return;
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Id == message.Id)
                    _rows[i] = ToRow(message) with { ShowDay = row.ShowDay, Continues = row.Continues };
                else if (row.ReplyTo is { Deleted: false } quote && quote.Id == message.Id)
                    _rows[i] = row with { ReplyTo = quote with { Summary = Shorten(message.Summary) } };
            }
        }

        void Remove(long messageId)
        {
            var index = _rows.ToList().FindIndex(row => row.Id == messageId);
            if (index >= 0)
            {
                if (_replyTo?.Id == messageId || _editing?.Id == messageId)
                    ClearContext();
                _rows.RemoveAt(index);
                if (index < _rows.Count)
                    Relink(index);
            }
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].ReplyTo is { Deleted: false } quote && quote.Id == messageId)
                    _rows[i] = _rows[i] with { ReplyTo = new ReplyPreview(messageId, "", "", Deleted: true) };
            }
        }

        /// <summary>One line for a message, as the chat list would put it.</summary>
        static string SummaryOf(MessageRow row) => Shorten(new MessageDto(row.Id ?? 0, default, new UserDto(default, "", row.Sender), row.Text,
            row.SentAt, row.ClientMessageId, Attachments: row.Attachments?.Select(item => item.File).ToList()).Summary);

        static string Shorten(string text)
        {
            var line = text.ReplaceLineEndings(" ");
            return line.Length <= 100 ? line : line[..99] + "…";
        }

        // ---- Going to a message: a quoted one, or one found by searching

        async void OnReplyTapped(MessageRow row)
        {
            if (row.ReplyTo is { Deleted: false } quote && _rows.Contains(row))
                await ShowMessageAsync(quote.Id);
        }

        /// <summary>Scrolls to the message, loading older pages until it is there; a long way back is given up.</summary>
        async Task ShowMessageAsync(long messageId)
        {
            if (_chat is not { } chat)
                return;
            for (var pages = 0; pages < 20; pages++)
            {
                var index = _rows.ToList().FindIndex(row => row.Id == messageId);
                if (index >= 0)
                {
                    Messages.ScrollTo(index, position: ScrollToPosition.Center, animate: true);
                    return;
                }
                if (_rows.FirstOrDefault(row => row.Id is not null)?.Id is not { } oldest || oldest < messageId)
                    break;
                try
                {
                    var page = await _api.GetMessagesAsync(chat.Id, oldest);
                    if (_chat?.Id != chat.Id || page.Count == 0)
                        break;
                    foreach (var message in page)
                        Merge(message);
                    OlderButton.IsVisible = page.Count == PageSize;
                }
                catch (ApiException error)
                {
                    ShowError(error.Message);
                    return;
                }
            }
            ShowError("Это сообщение уже не найти.");
        }

        // ---- Typing: said every few seconds while the user types, shown for a few seconds when others do

        readonly Dictionary<Guid, DateTime> _typing = [];
        long _typingSaidAt;
        IDispatcherTimer? _typingClock;

        void SayTyping()
        {
            if (_chat is not { } chat || Environment.TickCount64 - _typingSaidAt < 3000)
                return;
            _typingSaidAt = Environment.TickCount64;
            _ = _hub.TypingAsync(chat.Id);
        }

        void OnUserTyping(Guid chatId, Guid userId) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (chatId != _chat?.Id || userId == _auth.Session?.User.Id)
                return;
            _typing[userId] = DateTime.UtcNow.AddSeconds(5);
            if (_typingClock is null)
            {
                _typingClock = Dispatcher.CreateTimer();
                _typingClock.Interval = TimeSpan.FromSeconds(1);
                _typingClock.Tick += (_, _) =>
                {
                    ShowPresence();
                    if (_typing.Count == 0)
                        _typingClock?.Stop();
                };
            }
            _typingClock.Start();
            ShowPresence();
        });

        /// <summary>"печатает…", with the name in a group; null when nobody is typing now.</summary>
        string? TypingLine()
        {
            foreach (var gone in _typing.Where(pair => pair.Value < DateTime.UtcNow).Select(pair => pair.Key).ToList())
                _typing.Remove(gone);
            if (_typing.Count == 0 || _chat is not { } chat)
                return null;
            if (chat.Kind == ChatKind.Direct)
                return "печатает…";
            if (_typing.Count > 1)
                return $"{_typing.Count} печатают…";
            var name = chat.Members.FirstOrDefault(member => member.Id == _typing.Keys.First())?.DisplayName ?? "Кто-то";
            return $"{name} печатает…";
        }

        // ---- Searching the chat

        CancellationTokenSource? _searching;

        void OnSearchClicked(object? sender, EventArgs e)
        {
            if (_chat is null)
                return;
            SearchEntry.Text = "";
            SearchResults.ItemsSource = null;
            SearchEmpty.Text = "Введите слово или фразу";
            SearchEmpty.IsVisible = true;
            SearchBar.IsVisible = SearchPanel.IsVisible = true;
            SearchEntry.Focus();
        }

        void OnSearchCloseClicked(object? sender, EventArgs e) => CloseSearch();

        void CloseSearch()
        {
            _searching?.Cancel();
            _searching = null;
            SearchBar.IsVisible = SearchPanel.IsVisible = false;
        }

        // A moment after the last key: searching on every letter would only throw most answers away.
        async void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => await SearchAsync(e.NewTextValue, TimeSpan.FromMilliseconds(400));

        async void OnSearchTextCompleted(object? sender, EventArgs e) => await SearchAsync(SearchEntry.Text, TimeSpan.Zero);

        async Task SearchAsync(string? text, TimeSpan delay)
        {
            _searching?.Cancel();
            var searching = _searching = new CancellationTokenSource();
            var query = text?.Trim() ?? "";
            if (_chat is not { } chat || query.Length == 0)
            {
                SearchResults.ItemsSource = null;
                SearchEmpty.Text = "Введите слово или фразу";
                SearchEmpty.IsVisible = true;
                return;
            }
            try
            {
                await Task.Delay(delay, searching.Token);
                var found = await _api.SearchAsync(chat.Id, query, searching.Token);
                if (searching.IsCancellationRequested)
                    return;
                SearchResults.ItemsSource = found
                    .Select(message => new SearchHit(message.Id, message.Sender.DisplayName, Formatting.Time(message.SentAt), message.Summary))
                    .ToList();
                SearchEmpty.Text = "Ничего не найдено";
                SearchEmpty.IsVisible = found.Count == 0;
            }
            catch (OperationCanceledException)
            {
                // A newer search took over.
            }
            catch (ApiException error)
            {
                SearchEmpty.Text = error.Message;
                SearchEmpty.IsVisible = true;
            }
        }

        async void OnSearchResultSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not SearchHit hit)
                return;
            SearchResults.SelectedItem = null;
            CloseSearch();
            await ShowMessageAsync(hit.Id);
        }
    }
}
