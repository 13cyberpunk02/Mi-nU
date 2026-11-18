using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using MinU.Contracts;

namespace Mi_nU.Services
{
    /// <summary>The live connection to the server. Events are raised on a background thread.</summary>
    public class ChatHubClient(AuthService auth)
    {
        sealed class RetryForever : IRetryPolicy
        {
            static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

            public TimeSpan? NextRetryDelay(RetryContext context) =>
                context.PreviousRetryCount < Delays.Length ? Delays[context.PreviousRetryCount] : TimeSpan.FromSeconds(10);
        }

        HubConnection? _hub;
        CancellationTokenSource? _stop;

        public event Action<MessageDto>? MessageReceived;
        public event Action<ChatDto>? ChatCreated;
        /// <summary>A group's title, members or owner; take only those from it, see <see cref="IChatClient.ChatUpdated"/>.</summary>
        public event Action<ChatDto>? ChatUpdated;
        /// <summary>A contact, or the user on another device, changed their name or picture.</summary>
        public event Action<UserDto>? UserUpdated;
        public event Action<Guid>? ChatRemoved;
        public event Action<ReadReceipt>? ChatRead;
        public event Action<Presence>? PresenceChanged;
        /// <summary>Connected or reconnected: anything sent while offline has to be fetched again.</summary>
        public event Action? Connected;
        public event Action<CallInvite>? CallInvited;
        public event Action<CallParticipant>? CallParticipantJoined;
        public event Action<CallParticipant>? CallParticipantLeft;
        public event Action<Guid, UserDto>? CallDeclined;
        public event Action<Guid>? CallEnded;
        public event Action<CallSignal>? CallSignalReceived;

        /// <summary>Does nothing if already started.</summary>
        public void Start()
        {
            if (_hub is not null)
                return;

            var hub = new HubConnectionBuilder()
                .WithUrl($"{auth.ServerUrl}/hubs/chat", options =>
                    // Asked on every connect and reconnect, so an expired token is renewed first.
                    options.AccessTokenProvider = auth.GetAccessTokenAsync)
                .WithAutomaticReconnect(new RetryForever())
                .Build();

            hub.On<MessageDto>(nameof(IChatClient.MessageReceived), message => MessageReceived?.Invoke(message));
            hub.On<ChatDto>(nameof(IChatClient.ChatCreated), chat => ChatCreated?.Invoke(chat));
            hub.On<ChatDto>(nameof(IChatClient.ChatUpdated), chat => ChatUpdated?.Invoke(chat));
            hub.On<UserDto>(nameof(IChatClient.UserUpdated), user => UserUpdated?.Invoke(user));
            hub.On<Guid>(nameof(IChatClient.ChatRemoved), chatId => ChatRemoved?.Invoke(chatId));
            hub.On<ReadReceipt>(nameof(IChatClient.ChatRead), receipt => ChatRead?.Invoke(receipt));
            hub.On<Presence>(nameof(IChatClient.PresenceChanged), presence => PresenceChanged?.Invoke(presence));
            hub.On<CallInvite>(nameof(IChatClient.CallInvited), invite => CallInvited?.Invoke(invite));
            hub.On<CallParticipant>(nameof(IChatClient.CallParticipantJoined), participant => CallParticipantJoined?.Invoke(participant));
            hub.On<CallParticipant>(nameof(IChatClient.CallParticipantLeft), participant => CallParticipantLeft?.Invoke(participant));
            hub.On<Guid, UserDto>(nameof(IChatClient.CallDeclined), (callId, user) => CallDeclined?.Invoke(callId, user));
            hub.On<Guid>(nameof(IChatClient.CallEnded), callId => CallEnded?.Invoke(callId));
            hub.On<CallSignal>(nameof(IChatClient.CallSignalReceived), signal => CallSignalReceived?.Invoke(signal));
            hub.On<MessageDto>(nameof(IChatClient.MessageUpdated), message => MessageUpdated?.Invoke(message));
            hub.On<Guid, long>(nameof(IChatClient.MessageDeleted), (chatId, messageId) => MessageDeleted?.Invoke(chatId, messageId));
            hub.On<Guid, Guid>(nameof(IChatClient.UserTyping), (chatId, userId) => UserTyping?.Invoke(chatId, userId));
            hub.Reconnected += _ =>
            {
                Connected?.Invoke();
                return Task.CompletedTask;
            };

            _hub = hub;
            _stop = new CancellationTokenSource();
            _ = ConnectAsync(hub, _stop.Token);
        }

        /// <summary>A message's text was edited or its reactions changed.</summary>
        public event Action<MessageDto>? MessageUpdated;
        /// <summary>Its sender deleted a message, in the chat with the first id, for everyone.</summary>
        public event Action<Guid, long>? MessageDeleted;
        /// <summary>The user with the second id is typing in the chat with the first.</summary>
        public event Action<Guid, Guid>? UserTyping;

        /// <summary>Text, or files uploaded beforehand with an optional caption, optionally as a reply to <paramref name="replyToId"/>.</summary>
        public async Task<MessageDto> SendAsync(Guid chatId, string text, Guid clientMessageId, IReadOnlyList<Guid>? fileIds = null, long? replyToId = null)
        {
            // Right after signing in the connection is still being set up; a message typed that fast shouldn't fail.
            await WaitConnectedAsync(TimeSpan.FromSeconds(10));
            return await Connection().InvokeAsync<MessageDto>("Send", new SendMessageRequest(chatId, text, clientMessageId, fileIds, replyToId));
        }

        public Task<MessageDto> SendMessageAsync(Guid chatId, string text, Guid clientMessageId) => SendAsync(chatId, text, clientMessageId);

        /// <summary>Sends files uploaded into the chat beforehand, with an optional caption.</summary>
        public Task<MessageDto> SendAttachmentsAsync(Guid chatId, string text, Guid clientMessageId, IReadOnlyList<Guid> fileIds) =>
            SendAsync(chatId, text, clientMessageId, fileIds);

        public Task<MessageDto> EditMessageAsync(long messageId, string text) => Connection().InvokeAsync<MessageDto>("EditMessage", messageId, text);

        public Task DeleteMessageAsync(long messageId) => Connection().InvokeAsync("DeleteMessage", messageId);

        /// <summary>The user's reaction; null takes it back.</summary>
        public Task<MessageDto> ReactAsync(long messageId, string? emoji) => Connection().InvokeAsync<MessageDto>("React", messageId, emoji);

        public Task<MessageDto> ForwardAsync(long messageId, Guid toChatId) => Connection().InvokeAsync<MessageDto>("Forward", messageId, toChatId);

        /// <summary>Best effort: a missed "typing" only means the others don't see it for a moment.</summary>
        public Task TypingAsync(Guid chatId) => TryInvokeAsync("Typing", chatId);

        /// <summary>Best effort, see <see cref="TryInvokeAsync"/>: the open chat reloads and marks itself again on <see cref="Connected"/>.</summary>
        public Task MarkReadAsync(Guid chatId, long messageId) => TryInvokeAsync("MarkRead", chatId, messageId);

        /// <summary>Best effort, see <see cref="TryInvokeAsync"/>: the state is sent again on <see cref="Connected"/>.</summary>
        public Task SetActiveAsync(bool active) => TryInvokeAsync("SetActive", active);

        // For calls whose effect is restored after reconnecting anyway: offline they're simply skipped.
        async Task TryInvokeAsync(string method, params object?[] args)
        {
            if (_hub is not { State: HubConnectionState.Connected } hub)
                return;
            try
            {
                await hub.InvokeCoreAsync(method, args);
            }
            catch (Exception)
            {
                // Lost the connection on the way.
            }
        }

        public Task<CallJoinResult> StartCallAsync(Guid chatId, bool video) =>
            Connection().InvokeAsync<CallJoinResult>("StartCall", chatId, video);

        public Task<CallJoinResult> JoinCallAsync(Guid callId) =>
            Connection().InvokeAsync<CallJoinResult>("JoinCall", callId);

        public Task DeclineCallAsync(Guid callId) => Connection().InvokeAsync("DeclineCall", callId);

        public Task LeaveCallAsync(Guid callId) => Connection().InvokeAsync("LeaveCall", callId);

        public Task SendCallSignalAsync(Guid callId, string toParticipantId, string type, string payload) =>
            Connection().InvokeAsync("SendCallSignal", callId, toParticipantId, type, payload);

        /// <summary>For a call answered from a notification, when the app has only just started connecting.</summary>
        public async Task WaitConnectedAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (_hub is not { State: HubConnectionState.Connected } && DateTime.UtcNow < deadline)
                await Task.Delay(200);
        }

        HubConnection Connection() =>
            _hub is { State: HubConnectionState.Connected } hub ? hub : throw new InvalidOperationException("Not connected.");

        public async Task StopAsync()
        {
            var hub = _hub;
            _hub = null;
            _stop?.Cancel();
            if (hub is not null)
                await hub.DisposeAsync();
        }

        // Automatic reconnect only covers a connection that once succeeded, not the first attempt.
        async Task ConnectAsync(HubConnection hub, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await hub.StartAsync(stop);
                    Connected?.Invoke();
                    return;
                }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Renewing signs the user out if the session is really over; offline, it just waits for the next try.
                    if (await auth.RenewAfterRejectionAsync(auth.Session?.Token))
                        continue;
                    if (auth.Session is null)
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(5), stop).ContinueWith(_ => { });
                }
                catch (Exception) when (!stop.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stop);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }
}
