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
                    options.AccessTokenProvider = () => Task.FromResult(auth.Session?.Token))
                .WithAutomaticReconnect(new RetryForever())
                .Build();

            hub.On<MessageDto>(nameof(IChatClient.MessageReceived), message => MessageReceived?.Invoke(message));
            hub.On<ChatDto>(nameof(IChatClient.ChatCreated), chat => ChatCreated?.Invoke(chat));
            hub.On<CallInvite>(nameof(IChatClient.CallInvited), invite => CallInvited?.Invoke(invite));
            hub.On<CallParticipant>(nameof(IChatClient.CallParticipantJoined), participant => CallParticipantJoined?.Invoke(participant));
            hub.On<CallParticipant>(nameof(IChatClient.CallParticipantLeft), participant => CallParticipantLeft?.Invoke(participant));
            hub.On<Guid, UserDto>(nameof(IChatClient.CallDeclined), (callId, user) => CallDeclined?.Invoke(callId, user));
            hub.On<Guid>(nameof(IChatClient.CallEnded), callId => CallEnded?.Invoke(callId));
            hub.On<CallSignal>(nameof(IChatClient.CallSignalReceived), signal => CallSignalReceived?.Invoke(signal));
            hub.Reconnected += _ =>
            {
                Connected?.Invoke();
                return Task.CompletedTask;
            };

            _hub = hub;
            _stop = new CancellationTokenSource();
            _ = ConnectAsync(hub, _stop.Token);
        }

        public Task<MessageDto> SendMessageAsync(Guid chatId, string text, Guid clientMessageId) =>
            Connection().InvokeAsync<MessageDto>("SendMessage", chatId, text, clientMessageId);

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
                    MainThread.BeginInvokeOnMainThread(auth.Logout);
                    return;
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
