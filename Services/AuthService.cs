using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinU.Contracts;

namespace Mi_nU.Services
{
    /// <summary>The message is written for the user and can be shown as is.</summary>
    public class AuthException(string message) : Exception(message);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(RegisterRequest))]
    [JsonSerializable(typeof(LoginRequest))]
    [JsonSerializable(typeof(RefreshRequest))]
    [JsonSerializable(typeof(AuthResponse))]
    internal partial class AuthJsonContext : JsonSerializerContext;

    /// <summary>
    /// The signed-in session. The access token lives an hour and is renewed with the refresh token,
    /// shortly before it runs out or when the server turns it down.
    /// </summary>
    public class AuthService
    {
        const string SessionKey = "session";
        const string ServerUrlKey = "server_url";
        static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(2);

        readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
        readonly SemaphoreSlim _refreshing = new(1, 1);

        public AuthResponse? Session { get; private set; }

        /// <summary>The user signed out, or the server stopped accepting the session.</summary>
        public event Action? SignedOut;

#if DEBUG
        public string ServerUrl
        {
            get => Preferences.Default.Get(ServerUrlKey, ServerConfig.DefaultUrl);
            set => Preferences.Default.Set(ServerUrlKey, value.Trim().TrimEnd('/'));
        }
#else
        // An address typed into a debug build earlier must not stick to the released app.
        public string ServerUrl => ServerConfig.DefaultUrl;
#endif

        /// <summary>Loads the saved session. False means the user has to sign in.</summary>
        public async Task<bool> RestoreAsync()
        {
            try
            {
                var json = await SecureStorage.Default.GetAsync(SessionKey);
                var session = json is null ? null : JsonSerializer.Deserialize(json, AuthJsonContext.Default.AuthResponse);
                // A session saved by a version without refresh tokens lasts only as long as its access token.
                var usableUntil = session?.RefreshToken is null ? session?.ExpiresAt : session.RefreshExpiresAt;
                if (session is null || usableUntil <= DateTimeOffset.UtcNow)
                    return false;

                Session = session;
                return true;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
            {
                // Android can restore a backup that this device's key can't decrypt: start over.
                SecureStorage.Default.Remove(SessionKey);
                return false;
            }
        }

        /// <summary>A token to send with a request, renewed first if it is about to run out. Null when signed out.</summary>
        public async Task<string?> GetAccessTokenAsync()
        {
            if (Session is { } session && session.ExpiresAt - DateTimeOffset.UtcNow < RenewBefore)
                await RefreshAsync(staleToken: session.Token);
            return Session?.Token;
        }

        /// <summary>
        /// The server turned down <paramref name="rejectedToken"/>: renews the session if it can.
        /// False means the session is over and the user is signed out.
        /// </summary>
        public async Task<bool> RenewAfterRejectionAsync(string? rejectedToken)
        {
            await RefreshAsync(staleToken: rejectedToken);
            return Session is not null && Session.Token != rejectedToken;
        }

        public Task LoginAsync(string userName, string password) =>
            SignInAsync(http => http.PostAsJsonAsync($"{ServerUrl}/api/auth/login",
                new LoginRequest(userName, password), AuthJsonContext.Default.LoginRequest));

        public Task RegisterAsync(string userName, string displayName, string password) =>
            SignInAsync(http => http.PostAsJsonAsync($"{ServerUrl}/api/auth/register",
                new RegisterRequest(userName, displayName, password), AuthJsonContext.Default.RegisterRequest));

        /// <summary>Signing out on purpose: the server also ends this device's sign-in, so a copied token is useless.</summary>
        public async Task LogoutAsync()
        {
            if (Session?.RefreshToken is { } refresh)
            {
                try
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    (await _http.PostAsJsonAsync($"{ServerUrl}/api/auth/logout", new RefreshRequest(refresh),
                        AuthJsonContext.Default.RefreshRequest, cancel.Token)).Dispose();
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    // Offline: the sign-in still ends here, and on the server when the token expires.
                }
            }
            Logout();
        }

        /// <summary>Ends the session on this device only, for when the server has already rejected it.</summary>
        public void Logout()
        {
            if (Session is null)
                return;

            Session = null;
            SecureStorage.Default.Remove(SessionKey);
            SignedOut?.Invoke();
        }

        // One refresh at a time: requests that find the token stale together share one new token,
        // since the server accepts each refresh token only once.
        async Task RefreshAsync(string? staleToken)
        {
            await _refreshing.WaitAsync();
            try
            {
                if (Session is not { } session || session.Token != staleToken)
                    return;
                if (session.RefreshToken is null)
                {
                    Logout();
                    return;
                }

                HttpResponseMessage response;
                try
                {
                    response = await _http.PostAsJsonAsync($"{ServerUrl}/api/auth/refresh",
                        new RefreshRequest(session.RefreshToken), AuthJsonContext.Default.RefreshRequest);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    // Offline: keep the session; the next request tries again.
                    return;
                }

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        Logout();
                    else if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.AuthResponse) is { } renewed)
                        await SaveAsync(renewed);
                }
            }
            finally
            {
                _refreshing.Release();
            }
        }

        async Task SignInAsync(Func<HttpClient, Task<HttpResponseMessage>> request)
        {
            HttpResponseMessage response;
            try
            {
                response = await request(_http);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
            {
                throw new AuthException("Не удалось связаться с сервером. Проверьте подключение к сети.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new AuthException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "Неверный логин или пароль.",
                        HttpStatusCode.Conflict => "Этот логин уже занят.",
                        HttpStatusCode.TooManyRequests => "Слишком много попыток. Подождите минуту и повторите.",
                        HttpStatusCode.BadRequest => "Сервер не принял данные. Проверьте, правильно ли заполнены поля.",
                        _ => "На сервере произошла ошибка. Повторите попытку позже.",
                    });

                var session = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.AuthResponse)
                    ?? throw new AuthException("На сервере произошла ошибка. Повторите попытку позже.");
                await SaveAsync(session);
            }
        }

        async Task SaveAsync(AuthResponse session)
        {
            await SecureStorage.Default.SetAsync(SessionKey, JsonSerializer.Serialize(session, AuthJsonContext.Default.AuthResponse));
            Session = session;
        }
    }
}
