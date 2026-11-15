using Android.App;
using Android.Runtime;

namespace Mi_nU
{
#if DEBUG || ALLOW_CLEARTEXT
    // The development server has no HTTPS; a release build only talks to HTTPS,
    // unless built with -p:MinuAllowCleartext=true to try it against a local server.
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
