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
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services.AddSingleton<AppShell>();
            builder.Services.AddSingleton<Services.AuthService>();
            builder.Services.AddSingleton<Services.ChatApi>();
            builder.Services.AddSingleton<Services.ChatHubClient>();
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
