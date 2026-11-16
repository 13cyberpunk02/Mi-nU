namespace Mi_nU.Pages
{
    /// <summary>
    /// A photo on the whole screen, closed with its button, Escape on a computer or back on a phone.
    /// A tap on the picture doesn't close it: on Windows the release of the very click that opened it lands there.
    /// </summary>
    public class PhotoPage : ContentPage, IQueryAttributable
    {
        readonly Image _image = new() { Aspect = Aspect.AspectFit };
        bool _closing;

        public PhotoPage()
        {
            BackgroundColor = Colors.Black;
            Shell.SetNavBarIsVisible(this, false);
            var close = new Button
            {
                Text = Themes.Icons.Close,
                FontFamily = "Icons",
                FontSize = 24,
                TextColor = Colors.White,
                BackgroundColor = Color.FromRgba(0, 0, 0, 0.4),
                WidthRequest = 44,
                HeightRequest = 44,
                CornerRadius = 22,
                Padding = 0,
                Margin = new Thickness(12),
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
                AutomationId = "PhotoClose",
            };
            SemanticProperties.SetDescription(close, "Закрыть");
            close.Clicked += async (_, _) => await CloseAsync();
            Content = new Grid { Children = { _image, close } };
        }

        public void ApplyQueryAttributes(IDictionary<string, object> query)
        {
            _image.Source = ImageSource.FromFile((string)query["path"]);
            if (query.TryGetValue("name", out var name))
                SemanticProperties.SetDescription(_image, $"Фото {name}");
        }

        protected override void OnHandlerChanged()
        {
            base.OnHandlerChanged();
#if WINDOWS
            // Escape closes it, as a viewer on a computer is expected to.
            if (Handler?.PlatformView is Microsoft.UI.Xaml.UIElement view)
            {
                var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
                escape.Invoked += async (_, e) =>
                {
                    e.Handled = true;
                    await CloseAsync();
                };
                view.KeyboardAccelerators.Add(escape);
            }
#endif
        }

        // Only once: a second "back" would leave the chat itself.
        async Task CloseAsync()
        {
            if (_closing)
                return;
            _closing = true;
            await Shell.Current.GoToAsync("..");
        }
    }
}
