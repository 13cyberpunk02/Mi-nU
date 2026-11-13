using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Mi_nU.Droid;

namespace Mi_nU
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // The theme is applied before this activity exists, so the status bar icons are set again once it does.
            Themes.ThemeManager.UseStatusBarIcons(Microsoft.Maui.Controls.Application.Current?.RequestedTheme == Microsoft.Maui.ApplicationModel.AppTheme.Dark);
            OnTapped(Intent);
        }

        // A notification tapped while the app runs comes here instead of OnCreate (single top).
        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            OnTapped(intent);
        }

        protected override void OnResume()
        {
            base.OnResume();
            PushNotifier.AppVisible = true;
        }

        protected override void OnPause()
        {
            base.OnPause();
            PushNotifier.AppVisible = false;
        }

        void OnTapped(Intent? intent)
        {
            // A call opens the app over the lock screen and wakes the display, as the phone's own calls do.
            if (OperatingSystem.IsAndroidVersionAtLeast(27)
                && intent?.GetStringExtra(PushNotifier.ExtraAction) is PushNotifier.ActionShowCall or PushNotifier.ActionAnswer)
            {
                SetShowWhenLocked(true);
                SetTurnScreenOn(true);
            }
            PushNotifier.HandleIntent(this, intent);
        }
    }
}
