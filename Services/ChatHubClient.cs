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
            hub.Reconnected += _ =>
            {
                Connected?.Invoke();
                return Task.CompletedTask;
            };

            _hub = hub;
            _stop = new CancellationTokenSource();
            _ = ConnectAsync(hub, _stop.Token);
        }

        public Task<MessageDto> SendMessageAsync(Guid chatId, string text, Guid clientMessageId)
        {
            if (_hub is not { State: HubConnectionState.Connected } hub)
                throw new InvalidOperationException("Not connected.");
            return hub.InvokeAsync<MessageDto>("SendMessage", chatId, text, clientMessageId);
        }

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
