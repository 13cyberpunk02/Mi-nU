namespace Mi_nU.Pages
{
    /// <summary>A photo on the whole screen. A tap anywhere, or back, closes it.</summary>
    public class PhotoPage : ContentPage, IQueryAttributable
    {
        readonly Image _image = new() { Aspect = Aspect.AspectFit };

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
            _image.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await CloseAsync()) });
            Content = new Grid { Children = { _image, close } };
        }

        public void ApplyQueryAttributes(IDictionary<string, object> query)
        {
            _image.Source = ImageSource.FromFile((string)query["path"]);
            if (query.TryGetValue("name", out var name))
                SemanticProperties.SetDescription(_image, $"Фото {name}");
        }

        static Task CloseAsync() => Shell.Current.GoToAsync("..");
    }
}
