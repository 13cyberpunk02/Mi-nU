using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Pages
{
    /// <summary><see cref="CanRemove"/>: the viewer owns the group and this is someone else.</summary>
    public record MemberRow(UserDto User, string Details, bool CanRemove)
    {
        public string Name => User.DisplayName;
        public string RemoveDescription => $"Удалить {User.DisplayName} из группы";
    }

    public partial class GroupInfoPage : ContentPage, IQueryAttributable
    {
        readonly AuthService _auth;
        readonly ChatApi _api;
        readonly ChatHubClient _hub;
        readonly PresenceService _presence;
        ChatDto? _chat;

        public GroupInfoPage(AuthService auth, ChatApi api, ChatHubClient hub, PresenceService presence)
        {
            InitializeComponent();
            _auth = auth;
            _api = api;
            _hub = hub;
            _presence = presence;
            TitleEntry.MaxLength = Limits.ChatTitleMax;
        }

        public void ApplyQueryAttributes(IDictionary<string, object> query)
        {
            _chat = (ChatDto)query["chat"];
            TitleEntry.Text = _chat.Title;
            Show();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            _hub.ChatUpdated += OnChatUpdated;
            _hub.ChatRemoved += OnChatRemoved;
            _presence.Changed += OnPresenceChanged;
            // Back from adding people: the list has the group as it is now.
            if (_chat is { } chat && MainPage.Current?.Latest(chat.Id) is { } latest)
                Apply(latest);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _hub.ChatUpdated -= OnChatUpdated;
            _hub.ChatRemoved -= OnChatRemoved;
            _presence.Changed -= OnPresenceChanged;
        }

        void OnChatUpdated(ChatDto chat) => MainThread.BeginInvokeOnMainThread(() => Apply(chat));

        void OnChatRemoved(Guid chatId) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (chatId == _chat?.Id && Shell.Current.CurrentPage == this)
                // Past the chat page too: the user is no longer in the group.
                await Navigation.PopToRootAsync();
        });

        void OnPresenceChanged(Guid userId)
        {
            if (_chat?.Members.Any(member => member.Id == userId) == true)
                Show();
        }

        void Apply(ChatDto chat)
        {
            if (_chat?.Id != chat.Id)
                return;

            var renamedElsewhere = TitleEntry.Text?.Trim() == _chat.Title;
            _chat = _chat with { Title = chat.Title, Members = chat.Members, OwnerId = chat.OwnerId, AvatarId = chat.AvatarId };
            // A name being typed stays; an untouched field follows the new name.
            if (renamedElsewhere)
                TitleEntry.Text = chat.Title;
            Show();
        }

        void Show()
        {
            if (_chat is not { } chat)
                return;

            var me = _auth.Session?.User.Id;
            var iOwn = chat.OwnerId == me;
            GroupAvatar.Name = chat.Title;
            GroupAvatar.Seed = chat.Id.ToString();
            GroupAvatar.PictureId = chat.AvatarId;
            RemovePhotoButton.IsVisible = chat.AvatarId is not null;
            GroupName.Text = chat.Title;
            GroupSize.Text = $"Участников: {chat.Members.Count}";
            RenameButton.IsEnabled = CanRename();

            Members.ItemsSource = chat.Members
                // The owner first, then the user, then everyone else by name.
                .OrderByDescending(member => member.Id == chat.OwnerId)
                .ThenByDescending(member => member.Id == me)
                .ThenBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(member => new MemberRow(member, Details(member, chat, me), iOwn && member.Id != me))
                .ToList();
        }

        string Details(UserDto member, ChatDto chat, Guid? me)
        {
            var parts = new List<string>();
            if (member.Id == me)
                parts.Add("вы");
            if (member.Id == chat.OwnerId)
                parts.Add("владелец");
            if (member.Id != me && PresenceService.Describe(_presence.Of(member.Id)) is { Length: > 0 } presence)
                parts.Add(presence);
            return parts.Count > 0 ? string.Join(" · ", parts) : "@" + member.UserName;
        }

        bool CanRename()
        {
            var title = TitleEntry.Text?.Trim() ?? "";
            return _chat is not null && title.Length is > 0 and <= Limits.ChatTitleMax && title != _chat.Title;
        }

        void OnTitleChanged(object? sender, TextChangedEventArgs e) => RenameButton.IsEnabled = CanRename();

        async void OnRenameClicked(object? sender, EventArgs e)
        {
            if (_chat is not { } chat || !CanRename())
                return;

            RenameButton.IsEnabled = false;
            try
            {
                Apply(await _api.RenameGroupAsync(chat.Id, TitleEntry.Text!.Trim()));
                TitleEntry.Text = _chat!.Title;
                ErrorLabel.IsVisible = false;
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
                RenameButton.IsEnabled = CanRename();
            }
        }

        async void OnPickPhotoClicked(object? sender, EventArgs e)
        {
            if (_chat is not { } chat)
                return;
            var (picture, error) = await PicturePicker.PickAsync("Фото группы");
            if (error is not null)
                ShowError(error);
            if (picture is null)
                return;
            await ChangePhotoAsync(() => _api.SetGroupAvatarAsync(chat.Id, picture.Bytes, picture.ContentType, picture.Width, picture.Height));
        }

        async void OnRemovePhotoClicked(object? sender, EventArgs e)
        {
            if (_chat is not { } chat
                || !await DisplayAlertAsync("Удалить фото группы?", "Вместо него снова будут инициалы.", "Удалить", "Отмена"))
                return;
            await ChangePhotoAsync(() => _api.RemoveGroupAvatarAsync(chat.Id));
        }

        async Task ChangePhotoAsync(Func<Task<ChatDto>> change)
        {
            PickPhotoButton.IsEnabled = RemovePhotoButton.IsEnabled = false;
            try
            {
                Apply(await change());
                ErrorLabel.IsVisible = false;
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
            finally
            {
                PickPhotoButton.IsEnabled = RemovePhotoButton.IsEnabled = true;
            }
        }

        async void OnAddClicked(object? sender, EventArgs e)
        {
            if (_chat is { } chat)
                await Shell.Current.GoToAsync("newgroup", new ShellNavigationQueryParameters { ["addTo"] = chat });
        }

        async void OnRemoveClicked(object? sender, EventArgs e)
        {
            if (_chat is not { } chat || (sender as Button)?.CommandParameter is not UserDto member)
                return;
            if (!await DisplayAlertAsync("Удалить из группы?", $"{member.DisplayName} больше не увидит сообщения «{chat.Title}».", "Удалить", "Отмена"))
                return;

            try
            {
                await _api.RemoveMemberAsync(chat.Id, member.Id);
                ErrorLabel.IsVisible = false;
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
        }

        async void OnLeaveClicked(object? sender, EventArgs e)
        {
            if (_chat is not { } chat || _auth.Session?.User.Id is not { } me)
                return;
            var last = chat.Members.Count == 1;
            var warning = last
                ? "Вы последний участник: группа и вся переписка будут удалены."
                : chat.OwnerId == me ? "Права владельца перейдут другому участнику." : "Вернуться можно, только если вас добавят снова.";
            if (!await DisplayAlertAsync($"Выйти из «{chat.Title}»?", warning, "Выйти", "Отмена"))
                return;

            LeaveButton.IsEnabled = false;
            try
            {
                await _api.RemoveMemberAsync(chat.Id, me);
                // The server's ChatRemoved closes the chat everywhere; this page goes back to the list itself.
                await Navigation.PopToRootAsync();
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
                LeaveButton.IsEnabled = true;
            }
        }

        void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.IsVisible = true;
        }
    }
}
