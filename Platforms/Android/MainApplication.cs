using Android.App;
using Android.Runtime;

namespace Mi_nU
{
#if DEBUG
    // The development server has no HTTPS; a release build only talks to HTTPS.
    [Application(UsesCleartextTraffic = true)]
#else
    [Application]
#endif
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
