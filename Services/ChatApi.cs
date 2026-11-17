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
    [JsonSerializable(typeof(IceServersDto))]
    [JsonSerializable(typeof(RegisterDeviceRequest))]
    [JsonSerializable(typeof(List<Presence>))]
    [JsonSerializable(typeof(RenameGroupRequest))]
    [JsonSerializable(typeof(AddMembersRequest))]
    [JsonSerializable(typeof(FileDto))]
    [JsonSerializable(typeof(FileLinkDto))]
    [JsonSerializable(typeof(UserDto))]
    [JsonSerializable(typeof(UpdateProfileRequest))]
    internal partial class ChatJsonContext : JsonSerializerContext;

    public class ChatApi(AuthService auth)
    {
        readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
        // A photo on a slow connection takes longer than any other request.
        readonly HttpClient _uploads = new() { Timeout = TimeSpan.FromMinutes(5) };

        /// <summary>
        /// Uploads a file into the chat; it is seen by nobody until sent in a message. A picture's size in pixels goes
        /// along, so the chat can lay it out before it loads.
        /// </summary>
        public Task<FileDto> UploadFileAsync(Guid chatId, byte[] bytes, string name, string contentType,
            int? width = null, int? height = null, int? durationSeconds = null, CancellationToken cancel = default)
        {
            // Bytes rather than a stream, so the request can be sent again after renewing the sign-in.
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", name);
            if (width is { } w)
                form.Add(new StringContent(w.ToString(System.Globalization.CultureInfo.InvariantCulture)), "width");
            if (height is { } h)
                form.Add(new StringContent(h.ToString(System.Globalization.CultureInfo.InvariantCulture)), "height");
            if (durationSeconds is { } seconds)
                form.Add(new StringContent(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)), "duration");
            return SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/files", form, ChatJsonContext.Default.FileDto, cancel, _uploads);
        }

        /// <summary>A short-lived link to download the file from storage, for a member of its chat.</summary>
        public Task<FileLinkDto> GetFileLinkAsync(Guid fileId, CancellationToken cancel = default) =>
            SendAsync(HttpMethod.Get, $"/api/files/{fileId}/link", null, ChatJsonContext.Default.FileLinkDto, cancel);

        public Task<UserDto> UpdateProfileAsync(string displayName) =>
            SendAsync(HttpMethod.Put, "/api/me",
                JsonContent.Create(new UpdateProfileRequest(displayName), ChatJsonContext.Default.UpdateProfileRequest),
                ChatJsonContext.Default.UserDto);

        /// <summary>A new profile picture; the old one is removed from storage.</summary>
        public Task<UserDto> SetAvatarAsync(byte[] bytes, string contentType, int? width, int? height)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", contentType == "image/png" ? "avatar.png" : "avatar.jpg");
            if (width is { } w)
                form.Add(new StringContent(w.ToString(System.Globalization.CultureInfo.InvariantCulture)), "width");
            if (height is { } h)
                form.Add(new StringContent(h.ToString(System.Globalization.CultureInfo.InvariantCulture)), "height");
            return SendAsync(HttpMethod.Put, "/api/me/avatar", form, ChatJsonContext.Default.UserDto, default, _uploads);
        }

        public Task<UserDto> RemoveAvatarAsync() =>
            SendAsync(HttpMethod.Delete, "/api/me/avatar", null, ChatJsonContext.Default.UserDto);

        /// <summary>STUN and TURN servers with fresh credentials; fetched for every call, so they never expire mid-call.</summary>
        public Task<IceServersDto> GetIceServersAsync() =>
            SendAsync(HttpMethod.Get, "/api/ice-servers", null, ChatJsonContext.Default.IceServersDto);

        public Task<List<ChatDto>> GetChatsAsync() =>
            SendAsync(HttpMethod.Get, "/api/chats", null, ChatJsonContext.Default.ListChatDto);

        /// <summary>Presence of everyone the user shares a chat with.</summary>
        public Task<List<Presence>> GetPresenceAsync() =>
            SendAsync(HttpMethod.Get, "/api/presence", null, ChatJsonContext.Default.ListPresence);

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

        public Task<ChatDto> RenameGroupAsync(Guid chatId, string title) =>
            SendAsync(HttpMethod.Put, $"/api/chats/{chatId}/title",
                JsonContent.Create(new RenameGroupRequest(title), ChatJsonContext.Default.RenameGroupRequest),
                ChatJsonContext.Default.ChatDto);

        public Task<ChatDto> AddMembersAsync(Guid chatId, IReadOnlyList<Guid> userIds) =>
            SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/members",
                JsonContent.Create(new AddMembersRequest(userIds), ChatJsonContext.Default.AddMembersRequest),
                ChatJsonContext.Default.ChatDto);

        /// <summary>Removing oneself is leaving the group.</summary>
        public Task RemoveMemberAsync(Guid chatId, Guid userId) =>
            SendAsync(HttpMethod.Delete, $"/api/chats/{chatId}/members/{userId}");

        public Task<List<MessageDto>> GetMessagesAsync(Guid chatId, long? before = null) =>
            SendAsync(HttpMethod.Get, $"/api/chats/{chatId}/messages{(before is null ? "" : $"?before={before}")}", null, ChatJsonContext.Default.ListMessageDto);

        /// <summary>Tells the server where to send this user's push notifications.</summary>
        public Task RegisterDeviceAsync(string token, string platform) =>
            SendAsync(HttpMethod.Put, "/api/devices",
                JsonContent.Create(new RegisterDeviceRequest(token, platform), ChatJsonContext.Default.RegisterDeviceRequest));

        public Task UnregisterDeviceAsync(string token) =>
            SendAsync(HttpMethod.Delete, $"/api/devices?token={Uri.EscapeDataString(token)}");

        /// <summary>Declines a ringing call without the SignalR connection, for the button on its notification.</summary>
        public Task DeclineCallAsync(Guid callId) =>
            SendAsync(HttpMethod.Post, $"/api/calls/{callId}/decline");

        Task SendAsync(HttpMethod method, string path, HttpContent? content = null) =>
            SendAsync<object>(method, path, content, null);

        /// <summary>A null <paramref name="type"/> is for requests the server answers with no body.</summary>
        async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T>? type,
            CancellationToken cancel = default, HttpClient? http = null)
        {
            http ??= _http;
            for (var attempt = 0; ; attempt++)
            {
                var token = await auth.GetAccessTokenAsync();
                // Not disposed: that would dispose the content, which a retry sends again.
                var request = new HttpRequestMessage(method, auth.ServerUrl + path) { Content = content };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                try
                {
                    using var response = await http.SendAsync(request, cancel);
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        // The token may have run out on the way: renew it once and try again.
                        if (attempt == 0 && await auth.RenewAfterRejectionAsync(token))
                            continue;
                        auth.Logout();
                        throw new ApiException("Сессия истекла. Войдите снова.");
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new ApiException("На сервере произошла ошибка. Повторите попытку позже.");
                    if (type is null)
                        return default!;

                    return await response.Content.ReadFromJsonAsync(type, cancel)
                        ?? throw new ApiException("На сервере произошла ошибка. Повторите попытку позже.");
                }
                catch (Exception e) when (!cancel.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
                {
                    throw new ApiException("Не удалось связаться с сервером. Проверьте подключение к сети.");
                }
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
