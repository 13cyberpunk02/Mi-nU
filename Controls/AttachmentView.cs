using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Controls
{
    /// <summary>
    /// A file in a message. <see cref="LocalPath"/> is set while the sender's own copy is all there is, before upload,
    /// so the photo shows at once.
    /// </summary>
    public sealed record AttachmentItem(FileDto File, string? LocalPath = null);

    /// <summary>A photo in its proportions, loaded from the device or storage, or for other files a line with the name.</summary>
    public class AttachmentView : ContentView
    {
        const double MaxWidth = 260;
        const double MaxHeight = 320;
        const double MinSide = 80;

        public static readonly BindableProperty ItemProperty =
            BindableProperty.Create(nameof(Item), typeof(AttachmentItem), typeof(AttachmentView),
                propertyChanged: (view, _, value) => ((AttachmentView)view).Show(value as AttachmentItem));

        readonly Image _image = new() { Aspect = Aspect.AspectFill };
        readonly Label _placeholder = new() { FontFamily = "Icons", FontSize = 28, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        readonly Border _photo;
        readonly Border _file;
        readonly Label _fileName = new() { FontFamily = "InterSemiBold", FontSize = 14, LineBreakMode = LineBreakMode.MiddleTruncation, MaxLines = 1 };
        readonly Label _fileSize = new() { FontSize = 12 };
        CancellationTokenSource? _loading;

        public AttachmentView()
        {
            _placeholder.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            _photo = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Content = new Grid { Children = { _placeholder, _image } },
            };
            _photo.SetDynamicResource(BackgroundColorProperty, "Pill");

            var icon = new Label { Text = Themes.Icons.File, FontFamily = "Icons", FontSize = 22, VerticalOptions = LayoutOptions.Center };
            icon.SetDynamicResource(Label.TextColorProperty, "AccentText");
            _fileSize.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            _file = new Border
            {
                StrokeThickness = 0,
                Padding = new Thickness(10, 8),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Content = new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
                    ColumnSpacing = 10,
                    Children = { icon, new VerticalStackLayout { Children = { _fileName, _fileSize } }.Column(1) },
                },
                MaximumWidthRequest = MaxWidth,
            };
            _file.SetDynamicResource(BackgroundColorProperty, "Pill");

            // A voice message: a round play button, how far it has played, and its length.
            var button = new Border
            {
                WidthRequest = 36,
                HeightRequest = 36,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(),
                Content = _voiceIcon,
            };
            button.SetDynamicResource(BackgroundColorProperty, "Accent");
            _voiceIcon.SetDynamicResource(Label.TextColorProperty, "OnAccent");
            _voiceProgress.SetDynamicResource(ProgressBar.ProgressColorProperty, "Accent");
            _voiceTime.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            _voice = new Border
            {
                StrokeThickness = 0,
                Padding = new Thickness(6, 6, 12, 6),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 },
                WidthRequest = 240,
                AutomationId = "VoiceMessage",
                Content = new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                    ColumnSpacing = 10,
                    Children = { button, _voiceProgress.Column(1), _voiceTime.Column(2) },
                },
            };
            _voice.SetDynamicResource(BackgroundColorProperty, "Pill");
            Loaded += (_, _) => Player.Changed += ShowPlayback;
            Unloaded += (_, _) => Player.Changed -= ShowPlayback;

            GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await OpenAsync()) });
        }

        readonly Border _voice;
        readonly Label _voiceIcon = new()
        {
            Text = Themes.Icons.Play, FontFamily = "Icons", FontSize = 18,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
        };
        readonly ProgressBar _voiceProgress = new() { VerticalOptions = LayoutOptions.Center };
        readonly Label _voiceTime = new() { FontSize = 13, VerticalOptions = LayoutOptions.Center, AutomationId = "VoiceTime" };

        static VoicePlayer Player => IPlatformApplication.Current!.Services.GetRequiredService<VoicePlayer>();

        /// <summary>What the player knows this voice message by: its id, or before it is sent, the recording itself.</summary>
        static string? VoiceKey(AttachmentItem? item) => item is null ? null : item.File.Id != Guid.Empty ? item.File.Id.ToString() : item.LocalPath;

        /// <summary>The play button and progress follow the player, which plays one message at a time.</summary>
        void ShowPlayback()
        {
            if (Item is not { File.IsVoice: true } item)
                return;
            var length = item.File.DurationSeconds ?? 0;
            var current = VoiceKey(item) is { } key && Player.Current == key;
            // A screen reader hears the state, not the ticking position.
            var description = $"Голосовое сообщение, {Formatting.Duration(length)}"
                + (!current ? ", нажмите, чтобы прослушать" : Player.IsPlaying ? ", играет" : ", на паузе");
            if (SemanticProperties.GetDescription(this) != description)
                SemanticProperties.SetDescription(this, description);
            if (!current)
            {
                _voiceIcon.Text = Themes.Icons.Play;
                _voiceProgress.Progress = 0;
                _voiceTime.Text = Formatting.Duration(length);
                return;
            }
            var position = Player.Position;
            _voiceIcon.Text = Player.IsPlaying ? Themes.Icons.Pause : Themes.Icons.Play;
            _voiceProgress.Progress = length > 0 ? Math.Clamp(position.TotalSeconds / length, 0, 1) : 0;
            _voiceTime.Text = Formatting.Duration((int)position.TotalSeconds);
        }

        public AttachmentItem? Item
        {
            get => (AttachmentItem?)GetValue(ItemProperty);
            set => SetValue(ItemProperty, value);
        }

        static FileCache Cache => IPlatformApplication.Current!.Services.GetRequiredService<FileCache>();

        void Show(AttachmentItem? item)
        {
            // Rows are reused while scrolling: whatever was loading for the previous item no longer matters.
            _loading?.Cancel();
            _loading = null;
            _image.Source = null;
            if (item is null)
                return;

            if (item.File.IsVoice)
            {
                Content = _voice;
                ShowPlayback();
                return;
            }

            if (!item.File.IsImage)
            {
                _fileName.Text = item.File.Name;
                _fileSize.Text = SizeText(item.File.Size);
                Content = _file;
                SemanticProperties.SetDescription(this, $"Файл {item.File.Name}, {SizeText(item.File.Size)}");
                return;
            }

            var (width, height) = DisplaySize(item.File.Width, item.File.Height);
            _photo.WidthRequest = width;
            _photo.HeightRequest = height;
            _placeholder.Text = Themes.Icons.Image;
            Content = _photo;
            SemanticProperties.SetDescription(this, "Фото, нажмите, чтобы открыть");

            if (item.LocalPath is { } local)
            {
                _image.Source = ImageSource.FromFile(local);
                return;
            }
            var loading = _loading = new CancellationTokenSource();
            _ = LoadAsync(item, loading.Token);
        }

        async Task LoadAsync(AttachmentItem item, CancellationToken cancel)
        {
            try
            {
                var path = await Cache.GetAsync(item.File);
                if (!cancel.IsCancellationRequested)
                    _image.Source = ImageSource.FromFile(path);
            }
            catch (ApiException)
            {
                // Shown as a broken picture; a tap tries again.
                if (!cancel.IsCancellationRequested)
                    _placeholder.Text = Themes.Icons.ImageOff;
            }
        }

        /// <summary>A photo is opening or open: further taps, such as the rest of a double click, change nothing.</summary>
        static bool _opening;

        async Task OpenAsync()
        {
            if (Item is { File.IsVoice: true } voice)
            {
                await PlayAsync(voice);
                return;
            }
            if (Item is not { } item || _opening || Shell.Current is not { } shell || shell.CurrentPage is Pages.PhotoPage)
                return;
            _opening = true;
            try
            {
                if (item.File.IsImage)
                {
                    var path = item.LocalPath ?? await Cache.GetAsync(item.File);
                    _image.Source ??= ImageSource.FromFile(path);
                    await shell.GoToAsync("photo", new ShellNavigationQueryParameters { ["path"] = path, ["name"] = item.File.Name });
                }
                else
                {
                    // A large file takes a while to arrive; the line says so rather than seeming to ignore the tap.
                    _fileSize.Text = "Загрузка…";
                    var path = item.LocalPath ?? await Cache.GetAsync(item.File);
                    _fileSize.Text = SizeText(item.File.Size);
                    // Opened by whatever the system has for the type.
                    await Launcher.Default.OpenAsync(new OpenFileRequest(item.File.Name, new ReadOnlyFile(path)));
                }
            }
            catch (ApiException)
            {
                if (item.File.IsImage)
                    _placeholder.Text = Themes.Icons.ImageOff;
                else
                    _fileSize.Text = "Не загрузилось · нажмите ещё раз";
            }
            finally
            {
                _opening = false;
            }
        }

        bool _fetching;

        /// <summary>Plays, pauses or resumes; the first time, the recording is fetched and kept like any file.</summary>
        async Task PlayAsync(AttachmentItem item)
        {
            if (_fetching || VoiceKey(item) is not { } key)
                return;
            _fetching = true;
            try
            {
                string path;
                if (item.LocalPath is { } local)
                    path = local;
                else
                {
                    _voiceTime.Text = "…";
                    path = await Cache.GetAsync(item.File);
                }
                await Player.ToggleAsync(key, path);
            }
            catch (ApiException)
            {
                _voiceTime.Text = "Не загрузилось";
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A damaged file, or no way to play sound right now.
                System.Diagnostics.Debug.WriteLine($"Voice message didn't play: {e}");
                _voiceTime.Text = "Не воспроизводится";
            }
            finally
            {
                _fetching = false;
            }
        }

        /// <summary>The picture's own proportions, fitted into the bubble; a square when they aren't known.</summary>
        static (double Width, double Height) DisplaySize(int? pixelWidth, int? pixelHeight)
        {
            if (pixelWidth is not > 0 || pixelHeight is not > 0)
                return (220, 220);
            double ratio = (double)pixelHeight.Value / pixelWidth.Value;
            var width = Math.Min(MaxWidth, pixelWidth.Value);
            var height = width * ratio;
            if (height > MaxHeight)
            {
                height = MaxHeight;
                width = height / ratio;
            }
            return (Math.Max(width, MinSide), Math.Max(height, MinSide));
        }

        public static string SizeText(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} Б",
            < 1024 * 1024 => $"{bytes / 1024.0:0} КБ",
            _ => $"{bytes / 1024.0 / 1024.0:0.#} МБ",
        };
    }

    static class ViewExtensions
    {
        public static T Column<T>(this T view, int column) where T : BindableObject
        {
            Grid.SetColumn(view, column);
            return view;
        }
    }
}
