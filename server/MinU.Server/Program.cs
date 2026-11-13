using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MinU.Server;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(config.GetConnectionString("Default")));
builder.Services.AddScoped<ChatService>();
builder.Services.AddSingleton<CallRegistry>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

// Push needs the Firebase service account key; without it the app works, only without notifications.
builder.Services.AddSingleton<PushQueue>();
builder.Services.AddHostedService<PushWorker>();
builder.Services.AddSingleton<IPushSender>(services =>
{
    var logger = services.GetRequiredService<ILogger<FcmPushSender>>();
    if (config["Firebase:CredentialsFile"] is not { Length: > 0 } file)
    {
        logger.LogWarning("Firebase:CredentialsFile is not set: push notifications are off");
        return new NoPushSender();
    }
    try
    {
        return FcmPushSender.Create(file, logger);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
    {
        // A bad key must not take chats and calls down with it.
        logger.LogError(e, "Cannot read the Firebase key {File}: push notifications are off", file);
        return new NoPushSender();
    }
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = config["Jwt:Issuer"],
        ValidAudience = config["Jwt:Audience"],
        IssuerSigningKey = Endpoints.SigningKey(config),
    };
    options.Events = new JwtBearerEvents
    {
        // WebSockets can't send headers from every client, so SignalR passes the token in the query string.
        OnMessageReceived = context =>
        {
            if (context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                context.Token = context.Request.Query["access_token"];
            return Task.CompletedTask;
        },
        // A token outlives its account, for example after the account is deleted.
        OnTokenValidated = async context =>
        {
            var id = Guid.Parse(context.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            if (!await db.Users.AnyAsync(user => user.Id == id))
                context.Fail("The account no longer exists.");
        },
    };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(Endpoints.AuthRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("Auth:AttemptsPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
});

// An SDP offer with several tracks can outgrow SignalR's default limit of 32 KB.
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 128 * 1024);

// Behind a reverse proxy every request comes from the proxy, so sign-in limits would be shared by all users.
// Only for a server the internet reaches through the proxy alone: anyone else could fake the header.
var behindProxy = config.GetValue<bool>("ReverseProxy:Enabled");
if (behindProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

if (behindProxy)
    app.UseForwardedHeaders();

if (config.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuth();
app.MapApi();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();

public partial class Program;
