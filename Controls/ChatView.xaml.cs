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
        /// <summary>Someone else in the chat has seen this message; only kept for one's own messages.</summary>
        public bool Read { get; init; }

        /// <summary>A line about a change to the group, from the server.</summary>
        public bool IsSystem { get; init; }
        /// <summary>Photos and files sent with the message; <see cref="Text"/> is then their caption, possibly empty.</summary>
        public IReadOnlyList<AttachmentItem>? Attachments { get; init; }
        public bool HasAttachments => Attachments is { Count: > 0 };

        /// <summary>
        /// Photos only: they fill the bubble to its edges, all one width, with the caption and time in a strip below,
        /// so the bubble is as wide as the photos and a long caption wraps to them.
        /// </summary>
        public bool IsMedia => Attachments is { Count: > 0 } files && files.All(item => item.File.IsImage);
        // A narrow photo still leaves room for a caption.
        public double MediaWidth => !IsMedia ? -1
            : Attachments!.Count == 1 ? Math.Max(200, AttachmentView.DisplaySize(Attachments[0].File.Width, Attachments[0].File.Height).Width)
            : AttachmentView.MaxWidth;
        public IReadOnlyList<AttachmentItem>? Shown => IsMedia ? Attachments!.Select(item => item with { FitWidth = MediaWidth }).ToList() : Attachments;
        public Thickness BubblePadding => IsMedia ? new Thickness(0) : new Thickness(12, 8);
        public Thickness FooterPadding => IsMedia ? new Thickness(12, 6, 12, 8) : new Thickness(0);
        public Thickness NamePadding => IsMedia ? new Thickness(12, 8, 12, 6) : new Thickness(0);
        public double AttachmentSpacing => IsMedia ? 2 : 4;
        /// <summary>A round video message: no bubble, only the circle with the time under it.</summary>
        public bool IsVideoNote => Attachments is { Count: 1 } files && files[0].File.IsVideoNote;
        public AttachmentItem? FirstAttachment => Attachments is { Count: > 0 } files ? files[0] : null;
        public bool IsFailed => State == MessageState.Failed;
        public bool HasText => Text.Length > 0;
        /// <summary>Set for a call record, which shows as a call instead of text.</summary>
        public CallSummary? Call { get; init; }
        public string CallTitle => Call is null ? "" : Formatting.CallTitle(Call, IsMine);
        public string CallIcon => Call switch
        {
            { DurationSeconds: null } when !IsMine => Themes.Icons.PhoneMissed,
            { Video: true } => Themes.Icons.Video,
            _ when IsMine => Themes.Icons.PhoneOutgoing,
            _ => Themes.Icons.PhoneIncoming,
        };
        public bool CallMissed => Call is { DurationSeconds: null } && !IsMine;
        public string CallDetails => Call?.DurationSeconds is { } seconds
            ? $"{Formatting.Time(SentAt)} · {Formatting.Duration(seconds)}"
            : Formatting.Time(SentAt);
        public string CallDescription => $"{CallTitle}, {CallDetails}. Нажмите, чтобы позвонить";

        public bool ShowReceipt => IsMine && State == MessageState.Sent;
        public string Receipt => Read ? Themes.Icons.CheckCheck : Themes.Icons.Check;
        public string ReceiptDescription => Read ? "Прочитано" : "Отправлено";
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
        public DataTemplate? MineCall { get; set; }
        public DataTemplate? OtherCall { get; set; }
        public DataTemplate? System { get; set; }
        public DataTemplate? MineNote { get; set; }
        public DataTemplate? OtherNote { get; set; }

        protected override DataTemplate? OnSelectTemplate(object item, BindableObject container) => item switch
        {
            MessageRow { IsSystem: true } => System,
            MessageRow { Call: not null, IsMine: true } => MineCall,
            MessageRow { Call: not null } => OtherCall,
            MessageRow { IsVideoNote: true, IsMine: true } => MineNote,
            MessageRow { IsVideoNote: true } => OtherNote,
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
        readonly AppActivity _activity;
        readonly FileCache _files;
        readonly ObservableCollection<MessageRow> _rows = [];
        ChatDto? _chat;
        bool _active;
        /// <summary>The newest message someone else has read: one's own messages up to it get two ticks.</summary>
        long _readUpTo;
        /// <summary>The newest message this view has reported as seen, so it isn't reported twice.</summary>
        long _markedUpTo;

        public ChatView()
        {
            InitializeComponent();
            // Created from XAML, so the services come from the app rather than the constructor.
            var services = IPlatformApplication.Current!.Services;
            _auth = services.GetRequiredService<AuthService>();
            _api = services.GetRequiredService<ChatApi>();
            _hub = services.GetRequiredService<ChatHubClient>();
            _calls = services.GetRequiredService<CallService>();
            _activity = services.GetRequiredService<AppActivity>();
            _files = services.GetRequiredService<FileCache>();
            _presence = services.GetRequiredService<PresenceService>();
            _recorder = services.GetRequiredService<VoiceRecorder>();
            _player = services.GetRequiredService<VoicePlayer>();
            Messages.ItemsSource = _rows;
            MessageEntry.MaxLength = Limits.MessageMax;
            // "5 мин назад" has to keep counting while the chat stays open.
            _clock = Dispatcher.CreateTimer();
            _clock.Interval = TimeSpan.FromSeconds(30);
            _clock.Tick += (_, _) => ShowPresence();
        }

        readonly PresenceService _presence;
        readonly IDispatcherTimer _clock;

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

            _ = StopVoiceAsync();
            _chat = chat;
            _readUpTo = chat.ReadUpTo;
            _markedUpTo = 0;
            _rows.Clear();
            MessageEntry.Text = "";
            OlderButton.IsVisible = ErrorLabel.IsVisible = false;
            ShowHeader();
            if (_active)
                _ = LoadLatestAsync();
        }

        /// <summary>The open chat's title, members or owner changed; its messages stay as they are.</summary>
        public void Update(ChatDto chat)
        {
            if (_chat?.Id != chat.Id)
                return;

            _chat = _chat with { Title = chat.Title, Members = chat.Members, OwnerId = chat.OwnerId, AvatarId = chat.AvatarId };
            ShowHeader();
        }

        /// <summary>Empties the view, for a chat the user is no longer in.</summary>
        public void Close()
        {
            _ = StopVoiceAsync();
            _chat = null;
            _rows.Clear();
            MessageEntry.Text = "";
        }

        void ShowHeader()
        {
            if (_chat is not { } chat)
                return;

            HeaderName.Text = chat.Title;
            HeaderAvatar.Name = chat.Title;
            HeaderAvatar.Seed = new ChatRow(chat).Seed;
            HeaderAvatar.PictureId = new ChatRow(chat, _auth.Session?.User.Id ?? default).PictureId;
            GroupInfoButton.IsVisible = chat.Kind == ChatKind.Group;
            ShowPresence();
        }

        async void OnHeaderTapped(object? sender, EventArgs e)
        {
            if (_chat is { Kind: ChatKind.Group } chat)
                await Shell.Current.GoToAsync("groupinfo", new ShellNavigationQueryParameters { ["chat"] = chat });
        }

        void OnChatUpdated(ChatDto chat) => MainThread.BeginInvokeOnMainThread(() => Update(chat));

        /// <summary>Under the name: the other person's presence, or for a group its size and how many are online.</summary>
        void ShowPresence()
        {
            if (_chat is not { } chat)
                return;

            var me = _auth.Session?.User.Id;
            var others = chat.Members.Where(member => member.Id != me).ToList();
            string text;
            var online = false;
            if (chat.Kind == ChatKind.Group)
            {
                var count = others.Count(member => _presence.IsOnline(member.Id));
                text = count > 0 ? $"Участников: {chat.Members.Count} · в сети: {count}" : $"Участников: {chat.Members.Count}";
            }
            else
            {
                var presence = others.Count == 1 ? _presence.Of(others[0].Id) : null;
                text = PresenceService.Describe(presence);
                online = presence?.Online == true;
            }

            HeaderSubtitle.Text = text;
            HeaderSubtitle.IsVisible = text.Length > 0;
            HeaderSubtitle.SetDynamicResource(Label.TextColorProperty, online ? "AccentText" : "TextMuted");
        }

        void OnPresenceChanged(Guid userId)
        {
            if (_chat?.Members.Any(member => member.Id == userId) == true)
                ShowPresence();
        }

        public void Activate()
        {
            if (_active)
                return;

            _active = true;
            _hub.MessageReceived += OnMessageReceived;
            _hub.ChatRead += OnChatRead;
            _hub.Connected += OnConnected;
            _activity.Changed += MarkSeen;
            _presence.Changed += OnPresenceChanged;
            _hub.ChatUpdated += OnChatUpdated;
            _clock.Start();
            // The group may have changed while another page covered this one.
            if (_chat is { } chat && MainPage.Current?.Latest(chat.Id) is { } latest)
                Update(latest);
            ShowPresence();
            _ = LoadLatestAsync();
        }

        public void Deactivate()
        {
            if (!_active)
                return;

            _active = false;
            // A call or another page coming up ends whatever was being recorded or played; nothing half-made is sent.
            _ = StopVoiceAsync();
            _hub.MessageReceived -= OnMessageReceived;
            _hub.ChatRead -= OnChatRead;
            _hub.Connected -= OnConnected;
            _activity.Changed -= MarkSeen;
            _presence.Changed -= OnPresenceChanged;
            _hub.ChatUpdated -= OnChatUpdated;
            _clock.Stop();
        }

        /// <summary>
        /// Tells the server the user has seen everything loaded, but only while the chat is really in view:
        /// a message that arrives behind another window or with the phone in a pocket stays unread.
        /// </summary>
        void MarkSeen()
        {
            if (!_active || !_activity.IsActive || _chat is not { } chat)
                return;

            var newest = _rows.Select(row => row.Id ?? 0).DefaultIfEmpty().Max();
            if (newest <= _markedUpTo)
                return;

            _markedUpTo = newest;
            _ = _hub.MarkReadAsync(chat.Id, newest);
#if ANDROID
            Droid.PushNotifier.CancelChat(Platform.AppContext, chat.Id);
#endif
        }

        void OnChatRead(ReadReceipt receipt) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (receipt.ChatId == _chat?.Id && receipt.UserId != _auth.Session?.User.Id)
                AdvanceReadUpTo(receipt.MessageId);
        });

        void AdvanceReadUpTo(long messageId)
        {
            if (messageId <= _readUpTo)
                return;

            _readUpTo = messageId;
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i] is { IsMine: true, Read: false, Id: { } id } row && id <= messageId)
                    _rows[i] = row with { Read = true };
            }
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
                if (page.LastOrDefault(message => message.Sender.Id != _auth.Session?.User.Id) is { } reply)
                    AdvanceReadUpTo(reply.Id);
                MarkSeen();
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
            // Whoever replies has read everything before their reply.
            if (message.Sender.Id != _auth.Session?.User.Id)
                AdvanceReadUpTo(message.Id);
            MarkSeen();
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

        void OnMessageTextChanged(object? sender, TextChangedEventArgs e)
        {
            var typed = !string.IsNullOrWhiteSpace(e.NewTextValue);
            SendButton.IsVisible = typed;
            RecordButton.IsVisible = !typed;
            VideoNoteButton.IsVisible = !typed;
        }

        readonly VoiceRecorder _recorder;
        readonly VoicePlayer _player;
        IDispatcherTimer? _recordingClock;

        async void OnRecordClicked(object? sender, EventArgs e)
        {
            if (_chat is null || _recorder.IsRecording)
                return;
            // Recording over a message playing would record it too.
            _player.Stop();
            if (!await _recorder.StartAsync())
            {
                ShowError("Не удалось включить микрофон. Проверьте, что доступ к нему разрешён.");
                return;
            }
            ErrorLabel.IsVisible = false;
            Composer.IsVisible = false;
            RecordingBar.IsVisible = true;
            RecordingTime.Text = Formatting.Duration(0);
            ShowLiveWave();
            _recordingClock = Dispatcher.CreateTimer();
            // Often enough for the bars to move with the voice.
            _recordingClock.Interval = TimeSpan.FromMilliseconds(100);
            _recordingClock.Tick += async (_, _) =>
            {
                RecordingTime.Text = Formatting.Duration((int)_recorder.Elapsed.TotalSeconds);
                ShowLiveWave();
                // A very long one goes as it is rather than growing without end.
                if (_recorder.Elapsed >= VoiceRecorder.MaxLength)
                    await FinishRecordingAsync(send: true);
            };
            _recordingClock.Start();
        }

        /// <summary>One bar per moment heard, newest on the right once the row is full; dots where nothing is yet.</summary>
        void ShowLiveWave()
        {
            var bars = Math.Max(1, (int)((LiveWave.Width + 2) / 5));
            var recent = _recorder.RecentLevels(bars);
            var levels = new byte[bars];
            for (var i = 0; i < recent.Length; i++)
                levels[i] = (byte)Math.Round(recent[i] * 31);
            LiveWave.Levels = levels;
            LiveWave.Progress = (double)recent.Length / bars;
        }

        async void OnRecordCancelClicked(object? sender, EventArgs e) => await FinishRecordingAsync(send: false);

        async void OnRecordSendClicked(object? sender, EventArgs e) => await FinishRecordingAsync(send: true);

        async Task StopVoiceAsync()
        {
            _player.Stop();
            await FinishRecordingAsync(send: false);
            await FinishVideoNoteAsync(send: false);
        }

        readonly VideoNoteRecorder _noteRecorder = IPlatformApplication.Current!.Services.GetRequiredService<VideoNoteRecorder>();
        IDispatcherTimer? _noteClock;

        async void OnVideoNoteClicked(object? sender, EventArgs e)
        {
            if (_chat is null || _noteRecorder.IsRecording || _recorder.IsRecording)
                return;
            // The camera is busy in a call, and a message playing would end up in the recording.
            if (_calls.Phase != CallPhase.Idle)
            {
                ShowError("Во время звонка видеосообщение не записать.");
                return;
            }
            _player.Stop();
            MediaFocus.Take(this);
            NoteTime.Text = Formatting.Duration(0);
            NoteRing.Progress = 0;
            VideoNoteOverlay.IsVisible = true;
            // The preview's own view comes with the overlay; it must be there for the camera to draw into.
            await Task.Delay(50);
            if (await _noteRecorder.StartAsync(NotePreview) is { } problem)
            {
                VideoNoteOverlay.IsVisible = false;
                ShowError(problem);
                return;
            }
            ErrorLabel.IsVisible = false;
            _noteClock = Dispatcher.CreateTimer();
            _noteClock.Interval = TimeSpan.FromMilliseconds(100);
            _noteClock.Tick += async (_, _) =>
            {
                var elapsed = _noteRecorder.Elapsed;
                NoteTime.Text = Formatting.Duration((int)elapsed.TotalSeconds);
                NoteRing.Progress = elapsed / VideoNoteRecorder.MaxLength;
                // A minute at most, then it goes as it is.
                if (elapsed >= VideoNoteRecorder.MaxLength)
                    await FinishVideoNoteAsync(send: true);
            };
            _noteClock.Start();
        }

        async void OnVideoNoteCancelClicked(object? sender, EventArgs e) => await FinishVideoNoteAsync(send: false);

        async void OnVideoNoteSendClicked(object? sender, EventArgs e) => await FinishVideoNoteAsync(send: true);

        /// <summary>Back to the chat; the video goes out like a file, shown at once and retried the same way.</summary>
        async Task FinishVideoNoteAsync(bool send)
        {
            if (!_noteRecorder.IsRecording)
            {
                VideoNoteOverlay.IsVisible = false;
                return;
            }
            _noteClock?.Stop();
            _noteClock = null;
            if (!send || _chat is not { } chat)
            {
                await _noteRecorder.CancelAsync();
                VideoNoteOverlay.IsVisible = false;
                return;
            }

            var note = await _noteRecorder.StopAsync();
            VideoNoteOverlay.IsVisible = false;
            if (note is not { } video)
            {
                ShowError("Видеосообщение слишком короткое или не получилось.");
                return;
            }
            var bytes = await File.ReadAllBytesAsync(video.Path);
            if (_chat?.Id != chat.Id || bytes.Length > Limits.FileMax)
            {
                VoiceRecorder.TryDelete(video.Path);
                return;
            }
            const string name = "video.mp4";
            const string type = "video/mp4";
            var row = new MessageRow(null, Guid.NewGuid(), "", _auth.Session?.User.DisplayName ?? "", false, true, DateTimeOffset.UtcNow, MessageState.Sending)
            {
                Attachments = [new AttachmentItem(new FileDto(Guid.Empty, name, type, bytes.Length, DurationSeconds: video.Seconds), video.Path)],
            };
            _outgoing[row.ClientMessageId] = [new OutgoingFile(bytes, name, type, null, video.Path, video.Seconds)];
            _rows.Add(row);
            Relink(_rows.Count - 1);
            ScrollToEnd();
            await SendFilesAsync(chat, row);
        }

        /// <summary>Back to typing; the recording goes out like a file, so it shows at once and is retried the same way.</summary>
        async Task FinishRecordingAsync(bool send)
        {
            if (!_recorder.IsRecording)
                return;
            _recordingClock?.Stop();
            _recordingClock = null;
            RecordingBar.IsVisible = false;
            Composer.IsVisible = true;
            if (!send || _chat is not { } chat)
            {
                await _recorder.CancelAsync();
                return;
            }

            if (await _recorder.StopAsync() is not { } voice)
            {
                ShowError("Запись слишком короткая или не получилась.");
                return;
            }
            var bytes = await File.ReadAllBytesAsync(voice.Path);
            if (_chat?.Id != chat.Id)
            {
                VoiceRecorder.TryDelete(voice.Path);
                return;
            }
            // Empty when nothing was measured: the message then shows even bars.
            var waveform = voice.Waveform.Length > 0 ? Convert.ToBase64String(voice.Waveform) : null;
            const string name = "voice.m4a";
            const string type = "audio/mp4";
            var row = new MessageRow(null, Guid.NewGuid(), "", _auth.Session?.User.DisplayName ?? "", false, true, DateTimeOffset.UtcNow, MessageState.Sending)
            {
                Attachments = [new AttachmentItem(new FileDto(Guid.Empty, name, type, bytes.Length, DurationSeconds: voice.Seconds, Waveform: waveform), voice.Path)],
            };
            _outgoing[row.ClientMessageId] = [new OutgoingFile(bytes, name, type, null, voice.Path, voice.Seconds, waveform)];
            _rows.Add(row);
            Relink(_rows.Count - 1);
            ScrollToEnd();
            await SendFilesAsync(chat, row);
        }

        async void OnMessageSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not MessageRow row)
                return;

            Messages.SelectedItem = null;
            // A tap on a call calls back the same way.
            if (row.Call is { } call)
            {
                await StartCallAsync(call.Video);
                return;
            }
            if (row.State != MessageState.Failed || _chat is null)
                return;

            var sending = row with { State = MessageState.Sending };
            Replace(row, sending);
            if (_outgoing.ContainsKey(row.ClientMessageId))
                await SendFilesAsync(_chat, sending);
            else
                await SendAsync(_chat, sending);
        }

        /// <summary>A picked file on its way: kept with what is known of it, so a retry uploads only what didn't make it.</summary>
        sealed class OutgoingFile(byte[] bytes, string name, string contentType, (int Width, int Height)? size, string localPath,
            int? durationSeconds = null, string? waveform = null)
        {
            public int? DurationSeconds => durationSeconds;
            public string? Waveform => waveform;
            public byte[] Bytes => bytes;
            public string Name => name;
            public string ContentType => contentType;
            public (int Width, int Height)? Size => size;
            public string LocalPath => localPath;
            public FileDto? Uploaded { get; set; }
        }

        /// <summary>Files of messages not sent yet, by the message's client id.</summary>
        readonly Dictionary<Guid, List<OutgoingFile>> _outgoing = [];

        async void OnAttachClicked(object? sender, EventArgs e)
        {
            if (_chat is not { } chat)
                return;

            const string photo = "Фото";
            const string camera = "Камера";
            const string file = "Файл";
            var choices = DeviceInfo.Platform == DevicePlatform.Android && MediaPicker.Default.IsCaptureSupported
                ? new[] { photo, camera, file }
                : [photo, file];
            var choice = await Shell.Current.DisplayActionSheetAsync("Прикрепить", "Отмена", null, choices);
            var files = choice switch
            {
                photo => await PickMediaAsync(useCamera: false),
                camera => await PickMediaAsync(useCamera: true),
                file => await PickFilesAsync(),
                _ => [],
            };
            if (files.Count == 0 || _chat?.Id != chat.Id)
                return;

            // Whatever was typed goes with them as the caption.
            var caption = MessageEntry.Text?.Trim() ?? "";
            if (caption.Length > Limits.MessageMax)
                caption = caption[..Limits.MessageMax];
            MessageEntry.Text = "";
            var row = new MessageRow(null, Guid.NewGuid(), caption, _auth.Session?.User.DisplayName ?? "", false, true, DateTimeOffset.UtcNow, MessageState.Sending)
            {
                Attachments = files
                    .Select(outgoing => new AttachmentItem(
                        new FileDto(Guid.Empty, outgoing.Name, outgoing.ContentType, outgoing.Bytes.Length, outgoing.Size?.Width, outgoing.Size?.Height),
                        outgoing.LocalPath))
                    .ToList(),
            };
            _outgoing[row.ClientMessageId] = files;
            _rows.Add(row);
            Relink(_rows.Count - 1);
            ScrollToEnd();
            await SendFilesAsync(chat, row);
        }

        /// <summary>
        /// Photos from the gallery, or on a phone from the camera. Already made smaller, turned upright and stripped of
        /// their metadata by the picker: a photo's EXIF can hold where it was taken.
        /// </summary>
        async Task<List<OutgoingFile>> PickMediaAsync(bool useCamera)
        {
            var options = new MediaPickerOptions
            {
                Title = "Фото",
                SelectionLimit = AttachmentLimits.PerMessage,
                MaximumWidth = 2048,
                MaximumHeight = 2048,
                CompressionQuality = 85,
                RotateImage = true,
                PreserveMetaData = false,
            };

            List<FileResult> picked;
            try
            {
                // Cancelled pickers give null or nothing.
                picked = useCamera
                    ? await MediaPicker.Default.CapturePhotoAsync(options) is { } shot ? [shot] : []
                    : (await MediaPicker.Default.PickPhotosAsync(options) ?? []).Where(result => result is not null).ToList();
            }
            catch (PermissionException)
            {
                ShowError("Нет доступа к фото или камере. Разрешите его в настройках.");
                return [];
            }
            catch (FeatureNotSupportedException)
            {
                ShowError("На этом устройстве выбрать фото нельзя.");
                return [];
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Whatever else the system's picker throws, the chat stays open.
                ShowError("Не удалось открыть фото.");
                return [];
            }

            return await ReadAsync(picked, "photo.jpg");
        }

        /// <summary>Any files, sent as they are: documents, archives, or a photo at its full size.</summary>
        async Task<List<OutgoingFile>> PickFilesAsync()
        {
            List<FileResult> picked;
            try
            {
                picked = (await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Файлы" }) ?? [])
                    .Where(result => result is not null).ToList()!;
            }
            catch (PermissionException)
            {
                ShowError("Нет доступа к файлам. Разрешите его в настройках.");
                return [];
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ShowError("Не удалось открыть выбор файлов.");
                return [];
            }
            return await ReadAsync(picked, "file");
        }

        /// <summary>
        /// The picked files, read and copied aside: the picker's own copies may go away before the upload is done.
        /// Too many or too large are left out with a word about it.
        /// </summary>
        async Task<List<OutgoingFile>> ReadAsync(List<FileResult> picked, string fallbackName)
        {
            if (picked.Count > AttachmentLimits.PerMessage)
                ShowError($"Можно отправить до {AttachmentLimits.PerMessage} файлов за раз; остальные не отправлены.");

            var files = new List<OutgoingFile>();
            var folder = Path.Combine(FileSystem.CacheDirectory, "outgoing");
            Directory.CreateDirectory(folder);
            foreach (var result in picked.Take(AttachmentLimits.PerMessage))
            {
                var name = string.IsNullOrWhiteSpace(result.FileName) ? fallbackName : result.FileName;
                byte[] bytes;
                try
                {
                    await using var stream = await result.OpenReadAsync();
                    if (stream.CanSeek && stream.Length > Limits.FileMax)
                    {
                        ShowError($"«{name}» больше {Limits.FileMax / 1024 / 1024} МБ и не отправлен.");
                        continue;
                    }
                    using var memory = new MemoryStream();
                    await stream.CopyToAsync(memory);
                    bytes = memory.ToArray();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    ShowError($"Не удалось прочитать «{name}».");
                    continue;
                }
                if (bytes.Length == 0 || bytes.Length > Limits.FileMax)
                {
                    ShowError(bytes.Length == 0 ? $"«{name}» пустой и не отправлен." : $"«{name}» больше {Limits.FileMax / 1024 / 1024} МБ и не отправлен.");
                    continue;
                }

                var contentType = string.IsNullOrWhiteSpace(result.ContentType) || result.ContentType == "application/octet-stream"
                    ? ContentTypeOf(name)
                    : result.ContentType;
                var local = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(name));
                await File.WriteAllBytesAsync(local, bytes);
                files.Add(new OutgoingFile(bytes, name, contentType, contentType.StartsWith("image/") ? ImageSize.Of(bytes) : null, local));
            }
            return files;
        }

        // The system doesn't always say, Windows' picker for one; the server checks pictures again anyway.
        static string ContentTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".heic" => "image/heic",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".ogg" or ".oga" => "audio/ogg",
            ".wav" => "audio/wav",
            _ => "application/octet-stream",
        };

        async Task SendFilesAsync(ChatDto chat, MessageRow row)
        {
            if (!_outgoing.TryGetValue(row.ClientMessageId, out var files))
                return;
            try
            {
                foreach (var outgoing in files.Where(outgoing => outgoing.Uploaded is null))
                {
                    var file = await _api.UploadFileAsync(chat.Id, outgoing.Bytes, outgoing.Name, outgoing.ContentType,
                        outgoing.Size?.Width, outgoing.Size?.Height, outgoing.DurationSeconds, outgoing.Waveform);
                    // This device has the bytes already: they are never downloaded back.
                    _files.Put(file, outgoing.Bytes);
                    outgoing.Uploaded = file;
                }
                var sent = await _hub.SendAttachmentsAsync(chat.Id, row.Text, row.ClientMessageId, files.Select(outgoing => outgoing.Uploaded!.Id).ToList());
                _outgoing.Remove(row.ClientMessageId);
                foreach (var outgoing in files)
                    TryDelete(outgoing.LocalPath);
                if (_chat?.Id == chat.Id)
                    Merge(sent);
            }
            catch (Exception)
            {
                Replace(row, row with { State = MessageState.Failed });
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still open somewhere; the system clears the cache folder in time.
            }
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
                    Continues = sameDay && !previous!.IsSystem && !row.IsSystem
                        && previous.IsMine == row.IsMine && previous.Sender == row.Sender,
                };
                if (linked != row)
                    _rows[i] = linked;
            }
        }

        MessageRow ToRow(MessageDto message)
        {
            var mine = message.Sender.Id == _auth.Session?.User.Id;
            return new MessageRow(message.Id, message.ClientMessageId, message.Text, message.Sender.DisplayName,
                _chat?.Kind == ChatKind.Group && !mine, mine, message.SentAt, MessageState.Sent)
            {
                Read = mine && message.Id <= _readUpTo,
                Call = message.Kind == MessageKind.Call ? message.Call : null,
                IsSystem = message.Kind == MessageKind.System,
                Attachments = message.Attachments?.Select(file => new AttachmentItem(file)).ToList(),
            };
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
