using System.Reflection;

namespace Mi_nU.Services
{
    /// <summary>
    /// The server a build talks to, set when building: dotnet build -p:MinuServerUrl=https://api.example.com
    /// Debug builds also let the address be changed on the sign-in screen; release builds use only this one.
    /// </summary>
    public static class ServerConfig
    {
        const string LocalServer = "http://127.0.0.1:5199";

        public static string DefaultUrl { get; } =
            typeof(ServerConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "MinuServerUrl")?.Value is { Length: > 0 } url
                ? url.TrimEnd('/')
                : LocalServer;
    }
}
