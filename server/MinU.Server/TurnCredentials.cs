using System.Security.Cryptography;
using System.Text;
using MinU.Contracts;

namespace MinU.Server;

/// <summary>
/// Temporary TURN credentials in the form coturn checks with <c>use-auth-secret</c>: the username carries
/// its expiry time, and the password is that username signed with the secret shared with coturn.
/// </summary>
public static class TurnCredentials
{
    public static IceServersDto Issue(IConfiguration config, Guid userId)
    {
        var secret = config["Turn:Secret"];
        var urls = config.GetSection("Turn:Urls").Get<string[]>() ?? [];
        if (string.IsNullOrEmpty(secret) || urls.Length == 0)
            return new IceServersDto([], "", "", DateTimeOffset.UtcNow);

        var expires = DateTimeOffset.UtcNow.AddHours(config.GetValue("Turn:LifetimeHours", 12));
        var username = $"{expires.ToUnixTimeSeconds()}:{userId:N}";
        var signature = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username));
        return new IceServersDto(urls, username, Convert.ToBase64String(signature), expires);
    }
}
