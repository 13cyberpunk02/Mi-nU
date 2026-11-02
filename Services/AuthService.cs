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
    [JsonSerializable(typeof(AuthResponse))]
    internal partial class AuthJsonContext : JsonSerializerContext;

    public class AuthService
    {
        const string SessionKey = "session";
        const string ServerUrlKey = "server_url";
        const string DefaultServerUrl = "http://127.0.0.1:5199";

        readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public AuthResponse? Session { get; private set; }

        /// <summary>The user signed out, or the server stopped accepting the session.</summary>
        public event Action? SignedOut;

        public string ServerUrl
        {
            get => Preferences.Default.Get(ServerUrlKey, DefaultServerUrl);
            set => Preferences.Default.Set(ServerUrlKey, value.Trim().TrimEnd('/'));
        }

        /// <summary>Loads the saved session. False means the user has to sign in.</summary>
        public async Task<bool> RestoreAsync()
        {
            try
            {
                var json = await SecureStorage.Default.GetAsync(SessionKey);
                var session = json is null ? null : JsonSerializer.Deserialize(json, AuthJsonContext.Default.AuthResponse);
                if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
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

        public Task LoginAsync(string userName, string password) =>
            SendAsync(http => http.PostAsJsonAsync($"{ServerUrl}/api/auth/login",
                new LoginRequest(userName, password), AuthJsonContext.Default.LoginRequest));

        public Task RegisterAsync(string userName, string displayName, string password) =>
            SendAsync(http => http.PostAsJsonAsync($"{ServerUrl}/api/auth/register",
                new RegisterRequest(userName, displayName, password), AuthJsonContext.Default.RegisterRequest));

        public void Logout()
        {
            if (Session is null)
                return;

            Session = null;
            SecureStorage.Default.Remove(SessionKey);
            SignedOut?.Invoke();
        }

        async Task SendAsync(Func<HttpClient, Task<HttpResponseMessage>> request)
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
                await SecureStorage.Default.SetAsync(SessionKey, JsonSerializer.Serialize(session, AuthJsonContext.Default.AuthResponse));
                Session = session;
            }
        }
    }
}
