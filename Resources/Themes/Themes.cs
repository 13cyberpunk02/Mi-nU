namespace Mi_nU.Themes
{
    public partial class LightTheme : ResourceDictionary
    {
        public LightTheme() => InitializeComponent();
    }

    public partial class DarkTheme : ResourceDictionary
    {
        public DarkTheme() => InitializeComponent();
    }

    /// <summary>Keeps the light or dark color dictionary in step with the system theme.</summary>
    public static class ThemeManager
    {
        static ResourceDictionary? _current;

        public static void Apply(Application app)
        {
            var merged = app.Resources.MergedDictionaries;
            // Only the previous theme goes: Styles.xaml stays merged.
            if (_current is not null)
                merged.Remove(_current);

            var dark = app.RequestedTheme == AppTheme.Dark;
            _current = dark ? new DarkTheme() : new LightTheme();
            merged.Add(_current);
            UseStatusBarIcons(dark);
        }

        /// <summary>Android draws the status bar over the app: its icons must be dark on a light page and light on a dark one.</summary>
        public static void UseStatusBarIcons(bool darkTheme)
        {
#if ANDROID
            if (Platform.CurrentActivity?.Window is { } window && AndroidX.Core.View.WindowCompat.GetInsetsController(window, window.DecorView) is { } insets)
                insets.AppearanceLightStatusBars = !darkTheme;
#endif
        }
    }

    /// <summary>Lucide icon glyphs, drawn with the "Icons" font.</summary>
    public static class Icons
    {
        public const string Phone = "";
        public const string PhoneOff = "";
        public const string Video = "";
        public const string VideoOff = "";
        public const string Mic = "";
        public const string MicOff = "";
        public const string Send = "";
        public const string Back = "";
        public const string Plus = "";
        public const string Search = "";
        public const string Users = "";
        public const string Check = "";
        /// <summary>Two ticks: a message someone has read.</summary>
        public const string CheckCheck = "\uE392";
        public const string PhoneIncoming = "\uE13A";
        public const string PhoneOutgoing = "\uE13D";
        public const string PhoneMissed = "\uE13B";
        public const string SwitchCamera = "\uE180";
        public const string Volume = "\uE1AB";
        public const string Close = "";
        public const string LogOut = "";
    }
}
