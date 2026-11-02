using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MinU.Contracts;

namespace Mi_nU.Services
{
    /// <summary>The message is written for the user and can be shown as is.</summary>
    public class ApiException(string message) : Exception(message);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(CreateDirectChatRequest))]
    [JsonSerializable(typeof(CreateGroupChatRequest))]
    [JsonSerializable(typeof(ChatDto))]
    [JsonSerializable(typeof(List<ChatDto>))]
    [JsonSerializable(typeof(List<UserDto>))]
    [JsonSerializable(typeof(List<MessageDto>))]
    internal partial class ChatJsonContext : JsonSerializerContext;

    public class ChatApi(AuthService auth)
    {
        readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public Task<List<ChatDto>> GetChatsAsync() =>
            SendAsync(HttpMethod.Get, "/api/chats", null, ChatJsonContext.Default.ListChatDto);

        public Task<List<UserDto>> SearchUsersAsync(string query, CancellationToken cancel = default) =>
            SendAsync(HttpMethod.Get, $"/api/users?query={Uri.EscapeDataString(query)}", null, ChatJsonContext.Default.ListUserDto, cancel);

        public Task<ChatDto> CreateDirectChatAsync(Guid userId) =>
            SendAsync(HttpMethod.Post, "/api/chats/direct",
                JsonContent.Create(new CreateDirectChatRequest(userId), ChatJsonContext.Default.CreateDirectChatRequest),
                ChatJsonContext.Default.ChatDto);

        public Task<ChatDto> CreateGroupChatAsync(string title, IReadOnlyList<Guid> memberIds) =>
            SendAsync(HttpMethod.Post, "/api/chats/group",
                JsonContent.Create(new CreateGroupChatRequest(title, memberIds), ChatJsonContext.Default.CreateGroupChatRequest),
                ChatJsonContext.Default.ChatDto);

        public Task<List<MessageDto>> GetMessagesAsync(Guid chatId, long? before = null) =>
            SendAsync(HttpMethod.Get, $"/api/chats/{chatId}/messages{(before is null ? "" : $"?before={before}")}", null, ChatJsonContext.Default.ListMessageDto);

        async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> type, CancellationToken cancel = default)
        {
            using var request = new HttpRequestMessage(method, auth.ServerUrl + path) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Session?.Token);

            try
            {
                using var response = await _http.SendAsync(request, cancel);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    auth.Logout();
                    throw new ApiException("Сессия истекла. Войдите снова.");
                }
                if (!response.IsSuccessStatusCode)
                    throw new ApiException("На сервере произошла ошибка. Повторите попытку позже.");

                return await response.Content.ReadFromJsonAsync(type, cancel)
                    ?? throw new ApiException("На сервере произошла ошибка. Повторите попытку позже.");
            }
            catch (Exception e) when (!cancel.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                throw new ApiException("Не удалось связаться с сервером. Проверьте подключение к сети.");
            }
        }
    }

    /// <summary>Searches as the user types: waits for a pause and drops results of older queries.</summary>
    public class UserSearch(ChatApi api)
    {
        CancellationTokenSource? _current;

        /// <summary>Null if a newer search has replaced this one.</summary>
        public async Task<List<UserDto>?> SearchAsync(string text)
        {
            _current?.Cancel();
            var current = _current = new CancellationTokenSource();
            try
            {
                await Task.Delay(300, current.Token);
                return await api.SearchUsersAsync(text.Trim(), current.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }
}
