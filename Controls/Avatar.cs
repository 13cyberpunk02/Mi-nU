using Microsoft.Maui.Controls.Shapes;

namespace Mi_nU.Controls
{
    /// <summary>A round avatar with initials. The tint follows <see cref="Seed"/>, so a person keeps their color everywhere.</summary>
    public class Avatar : ContentView
    {
        public static readonly BindableProperty NameProperty =
            BindableProperty.Create(nameof(Name), typeof(string), typeof(Avatar), "", propertyChanged: (b, _, _) => ((Avatar)b).Update());
        public static readonly BindableProperty SeedProperty =
            BindableProperty.Create(nameof(Seed), typeof(string), typeof(Avatar), "", propertyChanged: (b, _, _) => ((Avatar)b).Update());
        public static readonly BindableProperty SizeProperty =
            BindableProperty.Create(nameof(Size), typeof(double), typeof(Avatar), 48.0, propertyChanged: (b, _, _) => ((Avatar)b).Update());

        const int Tints = 6;
        readonly Border _circle;
        readonly Label _initials;

        public Avatar()
        {
            _initials = new Label { FontFamily = "InterSemiBold", HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
            _circle = new Border { StrokeThickness = 0, StrokeShape = new Ellipse(), Content = _initials };
            Content = _circle;
            Update();
        }

        public string Name
        {
            get => (string)GetValue(NameProperty);
            set => SetValue(NameProperty, value);
        }

        /// <summary>Anything stable for this person or chat, such as its id.</summary>
        public string Seed
        {
            get => (string)GetValue(SeedProperty);
            set => SetValue(SeedProperty, value);
        }

        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        public static string InitialsOf(string name)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length switch
            {
                0 => "?",
                1 => words[0][..1].ToUpperInvariant(),
                _ => (words[0][..1] + words[1][..1]).ToUpperInvariant(),
            };
        }

        void Update()
        {
            _circle.WidthRequest = _circle.HeightRequest = Size;
            _initials.FontSize = Math.Round(Size * 0.36);
            _initials.Text = InitialsOf(Name ?? "");
            SemanticProperties.SetDescription(this, Name);

            // A stable hash: string.GetHashCode changes between runs.
            var tint = 0;
            foreach (var c in Seed ?? "")
                tint = (tint * 31 + c) % Tints;
            _circle.SetDynamicResource(BackgroundColorProperty, $"AvatarBackground{tint}");
            _initials.SetDynamicResource(Label.TextColorProperty, $"AvatarInk{tint}");
        }
    }
}
