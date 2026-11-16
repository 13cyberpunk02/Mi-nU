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

            GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await OpenAsync()) });
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

        async Task OpenAsync()
        {
            if (Item is not { } item)
                return;
            try
            {
                var path = item.LocalPath ?? await Cache.GetAsync(item.File);
                if (item.File.IsImage)
                {
                    _image.Source ??= ImageSource.FromFile(path);
                    await Shell.Current.GoToAsync("photo", new ShellNavigationQueryParameters { ["path"] = path, ["name"] = item.File.Name });
                }
                else
                {
                    // Opened by whatever the system has for the type.
                    await Launcher.Default.OpenAsync(new OpenFileRequest(item.File.Name, new ReadOnlyFile(path)));
                }
            }
            catch (ApiException)
            {
                _placeholder.Text = Themes.Icons.ImageOff;
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
