using Mi_nU.Controls;
using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Pages
{
    /// <summary>The user's own profile: picture, name, and signing out.</summary>
    public class ProfilePage : ContentPage
    {
        readonly AuthService _auth;
        readonly ChatApi _api;
        readonly PushNotifications _push;
        readonly Avatar _avatar = new() { Size = 120, HorizontalOptions = LayoutOptions.Center, AutomationId = "ProfileAvatar" };
        readonly Label _userName = new() { HorizontalTextAlignment = TextAlignment.Center };
        readonly Entry _name = new() { AutomationId = "ProfileName", Placeholder = "Имя" };
        readonly Button _save = new() { Text = "Сохранить", AutomationId = "ProfileSave", IsEnabled = false };
        readonly Button _pick = new() { Text = "Выбрать фото", AutomationId = "ProfilePickPhoto" };
        readonly Button _remove = new() { Text = "Удалить фото", AutomationId = "ProfileRemovePhoto" };
        readonly Label _error = new() { IsVisible = false, AutomationId = "ProfileError" };
        bool _busy;

        public ProfilePage(AuthService auth, ChatApi api, PushNotifications push)
        {
            _auth = auth;
            _api = api;
            _push = push;
            Title = "Профиль";

            _userName.Style = StyleOf("Caption");
            _error.Style = StyleOf("Error");
            _pick.Style = _remove.Style = StyleOf("SecondaryButton");
            _remove.SetDynamicResource(Button.TextColorProperty, "DangerText");
            _name.MaxLength = Limits.DisplayNameMax;
            _name.TextChanged += (_, _) => _save.IsEnabled = CanSave();
            _name.Completed += async (_, _) => await SaveNameAsync();
            _save.Clicked += async (_, _) => await SaveNameAsync();
            _pick.Clicked += async (_, _) => await PickPhotoAsync();
            _remove.Clicked += async (_, _) => await RemovePhotoAsync();

            // "Выйти" is also what the tests and screen readers look for.
            var logout = new Button { Text = "Выйти", AutomationId = "ProfileLogout", Style = StyleOf("SecondaryButton") };
            logout.SetDynamicResource(Button.TextColorProperty, "DangerText");
            logout.Clicked += async (_, _) => await LogoutAsync();

            var nameField = new Border { Style = StyleOf("Field"), Content = _name };
            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(16, 24),
                    Spacing = 16,
                    MaximumWidthRequest = 480,
                    Children =
                    {
                        _avatar,
                        _userName,
                        new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.Center, Children = { _pick, _remove } },
                        new Label { Text = "Имя", Style = StyleOf("FieldLabel") },
                        new Grid
                        {
                            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                            ColumnSpacing = 8,
                            Children = { nameField, _save.Column(1) },
                        },
                        _error,
                        new BoxView { HeightRequest = 8, Color = Colors.Transparent },
                        logout,
                    },
                },
            };
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            _auth.ProfileChanged += Show;
            Show();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _auth.ProfileChanged -= Show;
        }

        void Show()
        {
            if (_auth.Session?.User is not { } me)
                return;
            _avatar.Name = me.DisplayName;
            _avatar.Seed = me.DisplayName;
            _avatar.PictureId = me.AvatarId;
            _userName.Text = "@" + me.UserName;
            // A name being typed stays.
            if (!_name.IsFocused)
                _name.Text = me.DisplayName;
            _remove.IsVisible = me.AvatarId is not null;
            _save.IsEnabled = CanSave();
        }

        bool CanSave()
        {
            var name = _name.Text?.Trim() ?? "";
            return !_busy && name.Length is > 0 and <= Limits.DisplayNameMax && name != _auth.Session?.User.DisplayName;
        }

        async Task SaveNameAsync()
        {
            if (!CanSave())
                return;
            await RunAsync(async () => await _auth.UpdateProfileAsync(await _api.UpdateProfileAsync(_name.Text!.Trim())));
            _name.Unfocus();
            Show();
        }

        /// <summary>A small, upright picture without its metadata: it is shown to everyone the user meets.</summary>
        async Task PickPhotoAsync()
        {
            FileResult? picked;
            try
            {
                picked = (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
                {
                    Title = "Фото профиля",
                    SelectionLimit = 1,
                    MaximumWidth = 512,
                    MaximumHeight = 512,
                    CompressionQuality = 85,
                    RotateImage = true,
                    PreserveMetaData = false,
                }) ?? []).FirstOrDefault(result => result is not null);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ShowError(e is PermissionException ? "Нет доступа к фото. Разрешите его в настройках." : "Не удалось открыть фото.");
                return;
            }
            if (picked is null)
                return;

            byte[] bytes;
            await using (var stream = await picked.OpenReadAsync())
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory);
                bytes = memory.ToArray();
            }
            if (bytes.Length > Limits.AvatarMax)
            {
                ShowError($"Фото больше {Limits.AvatarMax / 1024 / 1024} МБ.");
                return;
            }
            var contentType = picked.ContentType is "image/png" ? "image/png" : "image/jpeg";
            var size = ImageSize.Of(bytes);
            await RunAsync(async () => await _auth.UpdateProfileAsync(await _api.SetAvatarAsync(bytes, contentType, size?.Width, size?.Height)));
        }

        async Task RemovePhotoAsync()
        {
            if (!await DisplayAlertAsync("Удалить фото?", "Вместо него снова будут инициалы.", "Удалить", "Отмена"))
                return;
            await RunAsync(async () => await _auth.UpdateProfileAsync(await _api.RemoveAvatarAsync()));
        }

        async Task LogoutAsync()
        {
            // While still signed in: the server must know whose notifications to stop.
            await _push.UnregisterAsync();
            // AppShell returns to the sign-in screen when the session ends.
            await _auth.LogoutAsync();
        }

        async Task RunAsync(Func<Task> action)
        {
            _busy = true;
            _pick.IsEnabled = _remove.IsEnabled = _save.IsEnabled = false;
            try
            {
                await action();
                _error.IsVisible = false;
            }
            catch (ApiException error)
            {
                ShowError(error.Message);
            }
            finally
            {
                _busy = false;
                _pick.IsEnabled = _remove.IsEnabled = true;
                Show();
            }
        }

        void ShowError(string message)
        {
            _error.Text = message;
            _error.IsVisible = true;
        }

        static Style StyleOf(string key) =>
            Application.Current!.Resources.TryGetValue(key, out var style) ? (Style)style : throw new KeyNotFoundException(key);
    }
}
