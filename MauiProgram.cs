using Maui.WebRTC;
using Microsoft.Extensions.Logging;

namespace Mi_nU
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseWebRTC()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("Inter-Regular.ttf", "Inter");
                    fonts.AddFont("Inter-Medium.ttf", "InterMedium");
                    fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
                    fonts.AddFont("Inter-Bold.ttf", "InterBold");
                    fonts.AddFont("Lucide.ttf", "Icons");
                });

            // The Field style draws the border, so entries drop the platform's own underline or frame.
            Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoChrome", (handler, _) =>
            {
#if ANDROID
                handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#elif WINDOWS
                // Focused and hover states come from Platforms/Windows/App.xaml: WinUI throws if a control's own resources are set here.
                handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
#endif
            });

            builder.Services.AddSingleton<AppShell>();
            builder.Services.AddSingleton<Services.AuthService>();
            builder.Services.AddSingleton<Services.ChatApi>();
            builder.Services.AddSingleton<Services.ChatHubClient>();
            builder.Services.AddSingleton<Services.CallService>();
            builder.Services.AddSingleton<Services.PushNotifications>();
            builder.Services.AddSingleton<Services.DesktopNotifications>();
            builder.Services.AddTransient<Pages.CallPage>();
            builder.Services.AddTransient<Pages.IncomingCallPage>();
            builder.Services.AddTransient<Pages.ChatPage>();
            builder.Services.AddTransient<Pages.NewChatPage>();
            builder.Services.AddTransient<Pages.NewGroupPage>();
            builder.Services.AddTransient<Pages.StartupPage>();
            builder.Services.AddTransient<Pages.LoginPage>();
            builder.Services.AddTransient<Pages.RegisterPage>();
            builder.Services.AddTransient<MainPage>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
