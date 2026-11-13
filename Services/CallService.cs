using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Maui.WebRTC;
using MinU.Contracts;

namespace Mi_nU.Services
{
    /// <summary>The message is written for the user and can be shown as is.</summary>
    public class CallException(string message) : Exception(message);

    public enum CallPhase { Idle, Incoming, Waiting, Connected }

    [JsonSerializable(typeof(IceCandidate))]
    internal partial class CallJsonContext : JsonSerializerContext;

    /// <summary>
    /// Runs one call at a time as a mesh: one connection per other participant, all sharing the local tracks.
    /// State changes only on the main thread, and so do the events.
    /// </summary>
    public class CallService
    {
        static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(45);

        readonly ChatHubClient _hub;
        readonly ChatApi _api;
        /// <summary>The server's STUN and TURN for the current call; empty means only a shared network connects.</summary>
        IceServer[] _iceServers = [];
        readonly Dictionary<string, CallPeer> _peers = [];
        PeerConnectionFactory? _factory;
        Guid? _callId;
        ChatDto? _chat;
        bool _hadOthers;
        CancellationTokenSource? _ringing;
        Task _cleanup = Task.CompletedTask;
        AudioTrack? _audio;
        VideoSource? _source;
        CameraCapturer? _camera;
        IDisposable? _testPattern;

        public CallService(AuthService auth, ChatHubClient hub, ChatApi api)
        {
            _hub = hub;
            _api = api;
            hub.CallInvited += invite => OnMainThread(() => OnInvited(invite));
            hub.CallParticipantJoined += participant => OnMainThread(() => OnParticipantJoined(participant));
            hub.CallParticipantLeft += participant => OnMainThread(() => OnParticipantLeft(participant));
            hub.CallDeclined += (callId, user) => OnMainThread(() => OnDeclined(callId));
            hub.CallEnded += callId => OnMainThread(() => OnEnded(callId));
            hub.CallSignalReceived += signal => OnMainThread(() => OnSignal(signal));
            // After a reconnect the server has already dropped this device from the call.
            hub.Connected += () => OnMainThread(() => Finish("Связь с сервером прерывалась, звонок завершён."));
            auth.SignedOut += () => OnMainThread(() => _ = HangUpAsync());
        }

        public CallPhase Phase { get; private set; }
        public CallInvite? Invite { get; private set; }
        public VideoTrack? LocalVideo { get; private set; }
        public IEnumerable<CallPeer> Peers => _peers.Values;
        /// <summary>Who the call is with, for the call screen's header.</summary>
        public string Title { get; private set; } = "";
        public bool IsMuted => _audio is { Enabled: false };
        public bool IsCameraOff => LocalVideo is { Enabled: false };

        public event Action? Changed;
        public event Action? IncomingCall;
        /// <summary>The call is over or the incoming call is gone. The argument explains why, if the user should know.</summary>
        public event Action<string?>? Finished;

        public async Task StartAsync(ChatDto chat, bool video)
        {
            EnsureIdle();
            _chat = chat;
            Title = chat.Title;
            await JoinAsync(video, () => _hub.StartCallAsync(chat.Id, video));
            if (_peers.Count == 0)
                _ = FinishIfNobodyAnswersAsync(_ringing = new CancellationTokenSource());
        }

        /// <summary><paramref name="video"/> false answers a video call without the camera.</summary>
        public async Task AcceptAsync(bool video = true)
        {
            var invite = Invite ?? throw new CallException("Звонок уже завершён.");
            Invite = null;
            Title = invite.Caller.DisplayName;
            await _hub.WaitConnectedAsync(TimeSpan.FromSeconds(15));
            await JoinAsync(invite.Video && video, () => _hub.JoinCallAsync(invite.CallId));
        }

        /// <summary>
        /// A call the user tapped on a notification while the app was closed: SignalR's invite went nowhere then.
        /// <paramref name="ring"/> false skips the incoming call screen, for "Answer" on the notification.
        /// </summary>
        public void ShowIncomingFromPush(CallInvite invite, bool ring)
        {
            // The open app already got the same invite through SignalR, or the user is in another call.
            if (Phase != CallPhase.Idle)
                return;

            Invite = invite;
            Phase = CallPhase.Incoming;
            if (ring)
                IncomingCall?.Invoke();
        }

        public async Task DeclineAsync()
        {
            if (Invite is not { } invite)
                return;

            Invite = null;
            Phase = CallPhase.Idle;
            await SendQuietlyAsync(() => _hub.DeclineCallAsync(invite.CallId));
        }

        public async Task HangUpAsync()
        {
            if (_callId is { } callId)
                await SendQuietlyAsync(() => _hub.LeaveCallAsync(callId));
            Finish(null);
        }

        public void SetMuted(bool muted)
        {
            if (_audio is not null)
                _audio.Enabled = !muted;
            Changed?.Invoke();
        }

        public void SetCameraOff(bool off)
        {
            if (LocalVideo is not null)
                LocalVideo.Enabled = !off;
            Changed?.Invoke();
        }

#if DEBUG
        public async Task<string> DebugStatsAsync()
        {
            var lines = new List<string>();
            foreach (var peer in _peers.Values.ToList())
                { try { lines.Add($"{peer.Name}: {await peer.DebugStatsAsync()}"); } catch (ObjectDisposedException) { } }
            return string.Join("\n", lines);
        }
#endif

        void EnsureIdle()
        {
            if (Phase != CallPhase.Idle)
                throw new CallException("Сначала завершите текущий звонок.");
        }

        async Task JoinAsync(bool video, Func<Task<CallJoinResult>> join)
        {
            Phase = CallPhase.Waiting;
            try
            {
                await _cleanup;
                _iceServers = await GetIceServersAsync();
                await StartMediaAsync(video);
                var result = await join();
                _callId = result.CallId;
                foreach (var other in result.Others)
                    AddPeer(other).Offer();
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                Finish(null);
                throw e switch
                {
                    CallException known => known,
                    // A call answered from a notification may have ended while the app was starting.
                    Microsoft.AspNetCore.SignalR.HubException hub when hub.Message.Contains("has ended") => new CallException("Звонок уже завершён."),
                    _ => new CallException("Не удалось начать звонок. Проверьте подключение к сети."),
                };
            }
        }

        async Task<IceServer[]> GetIceServersAsync()
        {
            try
            {
                var ice = await _api.GetIceServersAsync();
                return ice.Urls.Select(url => new IceServer(url, ice.Username, ice.Credential)).ToArray();
            }
            catch (ApiException)
            {
                // Without them a call still connects on a shared network, so it goes ahead.
                return [];
            }
        }

        async Task StartMediaAsync(bool video)
        {
            if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
                throw new CallException("Для звонка нужен доступ к микрофону.");
            var cameraAllowed = video && await Permissions.RequestAsync<Permissions.Camera>() == PermissionStatus.Granted;

            _factory ??= new PeerConnectionFactory();
            _audio = _factory.CreateAudioTrack();

#if DEBUG
            // Lets the app be tested on a computer without using its real camera.
            var testPattern = Environment.GetEnvironmentVariable("MINU_TEST_PATTERN") == "1";
#else
            const bool testPattern = false;
#endif
            if (video && (testPattern || (cameraAllowed && CameraCapturer.DeviceCount > 0)))
            {
                _source = _factory.CreateVideoSource();
                LocalVideo = _factory.CreateVideoTrack(_source);
                if (testPattern)
                {
                    _testPattern = new TestPatternCapturer(_source);
                }
                else
                {
                    _camera = new CameraCapturer();
                    _camera.Start(_source);
                }
            }

            UseCallAudio(true, speaker: video);
            DeviceDisplay.Current.KeepScreenOn = true;
        }

        CallPeer AddPeer(CallParticipant participant)
        {
            if (_peers.TryGetValue(participant.ParticipantId, out var existing))
                return existing;

            var connection = _factory!.CreatePeerConnection(_iceServers);
            connection.AddTrack(_audio!);
            if (LocalVideo is not null)
                connection.AddTrack(LocalVideo);

            var peer = new CallPeer(participant.ParticipantId, participant.User.DisplayName, connection,
                (type, payload) => _hub.SendCallSignalAsync(participant.CallId, participant.ParticipantId, type, payload));
            peer.Changed += () => OnMainThread(() => OnPeerChanged(peer));
            _peers.Add(peer.Id, peer);

            _ringing?.Cancel();
            _hadOthers = true;
            Phase = CallPhase.Connected;
            return peer;
        }

        void OnInvited(CallInvite invite)
        {
            if (Phase != CallPhase.Idle)
            {
                // Already talking: the other side hears that this person can't answer.
                _ = SendQuietlyAsync(() => _hub.DeclineCallAsync(invite.CallId));
                return;
            }

            Invite = invite;
            Phase = CallPhase.Incoming;
            IncomingCall?.Invoke();
        }

        void OnParticipantJoined(CallParticipant participant)
        {
            if (IsCurrent(participant.CallId))
                AddPeer(participant);
            Changed?.Invoke();
        }

        void OnParticipantLeft(CallParticipant participant)
        {
            if (!IsCurrent(participant.CallId))
                return;

            RemovePeer(participant.ParticipantId);
            if (_hadOthers && _peers.Count == 0)
                _ = HangUpAsync();
            Changed?.Invoke();
        }

        void OnDeclined(Guid callId)
        {
            // In a group the others may still answer.
            if (IsCurrent(callId) && _chat?.Kind == ChatKind.Direct && _peers.Count == 0)
            {
                _ = SendQuietlyAsync(() => _hub.LeaveCallAsync(callId));
                Finish("Собеседник отклонил звонок.");
            }
        }

        void OnEnded(Guid callId)
        {
            if (Invite?.CallId == callId)
            {
                Invite = null;
                Phase = CallPhase.Idle;
                Finished?.Invoke(null);
            }
            else if (_callId == callId)
            {
                Finish(null);
            }
        }

        void OnSignal(CallSignal signal)
        {
            if (!IsCurrent(signal.CallId))
                return;

            _peers.GetValueOrDefault(signal.FromParticipantId)?.Receive(signal.Type, signal.Payload);
        }

        void OnPeerChanged(CallPeer peer)
        {
            if (peer.State == PeerConnectionState.Failed && _peers.ContainsKey(peer.Id))
            {
                RemovePeer(peer.Id);
                if (_peers.Count == 0)
                {
                    _ = HangUpAsync();
                    return;
                }
            }
            Changed?.Invoke();
        }

        bool IsCurrent(Guid callId) =>
            Phase is CallPhase.Waiting or CallPhase.Connected && (_callId is null || _callId == callId);

        void RemovePeer(string id)
        {
            if (!_peers.Remove(id, out var peer))
                return;

            // Tiles have to let go of the remote tracks before the connection disposes them.
            Changed?.Invoke();
            var previous = _cleanup;
            _cleanup = Task.Run(async () =>
            {
                await previous;
                await peer.DisposeAsync();
            });
        }

        async Task FinishIfNobodyAnswersAsync(CancellationTokenSource ringing)
        {
            try
            {
                await Task.Delay(RingTimeout, ringing.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_callId is { } callId)
                await SendQuietlyAsync(() => _hub.LeaveCallAsync(callId));
            Finish("Никто не ответил.");
        }

        void Finish(string? reason)
        {
            if (Phase is CallPhase.Idle or CallPhase.Incoming)
                return;

            _ringing?.Cancel();
            _ringing = null;
            _callId = null;
            _chat = null;
            _hadOthers = false;
            Phase = CallPhase.Idle;

            var peers = _peers.Values.ToList();
            _peers.Clear();
            var (camera, testPattern, audio, video, source) = (_camera, _testPattern, _audio, LocalVideo, _source);
            (_camera, _testPattern, _audio, LocalVideo, _source) = (null, null, null, null, null);

            // Views let go of the tracks here, before anything below disposes them.
            Changed?.Invoke();
            Finished?.Invoke(reason);

            UseCallAudio(false, speaker: false);
            DeviceDisplay.Current.KeepScreenOn = false;

            var previous = _cleanup;
            _cleanup = Task.Run(async () =>
            {
                await previous;
                camera?.Dispose();
                testPattern?.Dispose();
                foreach (var peer in peers)
                    await peer.DisposeAsync();
                audio?.Dispose();
                video?.Dispose();
                source?.Dispose();
            });
        }

        static async Task SendQuietlyAsync(Func<Task> send)
        {
            try
            {
                await send();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The server notices a lost connection by itself, so there is nothing to retry.
            }
        }

        static void OnMainThread(Action action) => MainThread.BeginInvokeOnMainThread(action);

        static void UseCallAudio(bool inCall, bool speaker)
        {
#if ANDROID
            // Without this mode Android applies no echo cancellation and keeps the output at media volume.
            var context = global::Android.App.Application.Context;
            var audio = (global::Android.Media.AudioManager)context.GetSystemService(global::Android.Content.Context.AudioService)!;
            audio.Mode = inCall ? global::Android.Media.Mode.InCommunication : global::Android.Media.Mode.Normal;
#pragma warning disable CA1422 // Its replacement, SetCommunicationDevice, only exists from Android 12.
            audio.SpeakerphoneOn = speaker;
#pragma warning restore CA1422
#endif
        }
    }

    /// <summary>The connection to one other participant.</summary>
    public sealed class CallPeer
    {
        readonly PeerConnection _connection;
        readonly Func<string, string, Task> _send;
        readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>();
        readonly Task _worker;

        internal CallPeer(string id, string name, PeerConnection connection, Func<string, string, Task> send)
        {
            Id = id;
            Name = name;
            _connection = connection;
            _send = send;
            _worker = Task.Run(ProcessWorkAsync);

            connection.IceCandidateGathered += candidate => Enqueue(() =>
                _send("candidate", JsonSerializer.Serialize(candidate, CallJsonContext.Default.IceCandidate)));
            connection.TrackReceived += track =>
            {
                if (track is VideoTrack video)
                {
                    Video = video;
                    Changed?.Invoke();
                }
            };
            connection.ConnectionStateChanged += state =>
            {
                State = state;
                Changed?.Invoke();
            };
        }

        public string Id { get; }
        public string Name { get; }
        public VideoTrack? Video { get; private set; }
        public PeerConnectionState State { get; private set; } = PeerConnectionState.New;

        internal event Action? Changed;

        internal void Offer() => Enqueue(async () =>
        {
            var offer = await _connection.CreateOfferAsync();
            // Sent first so that our candidates, which start with SetLocalDescription, arrive after it.
            await _send(offer.Type, offer.Sdp);
            await _connection.SetLocalDescriptionAsync(offer);
        });

        // Handled strictly one at a time, so a candidate can't overtake its description.
        internal void Receive(string type, string payload) => Enqueue(async () =>
        {
            switch (type)
            {
                case "offer":
                    await _connection.SetRemoteDescriptionAsync(new SessionDescription(type, payload));
                    var answer = await _connection.CreateAnswerAsync();
                    await _send(answer.Type, answer.Sdp);
                    await _connection.SetLocalDescriptionAsync(answer);
                    break;
                case "answer":
                    await _connection.SetRemoteDescriptionAsync(new SessionDescription(type, payload));
                    break;
                case "candidate":
                    await _connection.AddIceCandidateAsync(JsonSerializer.Deserialize(payload, CallJsonContext.Default.IceCandidate)!);
                    break;
            }
        });

        internal async Task DisposeAsync()
        {
            _work.Writer.TryComplete();
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // A step is stuck waiting for the server; disposing the connection doesn't depend on it.
            }
            _connection.Dispose();
        }

#if DEBUG
        internal async Task<string> DebugStatsAsync()
        {
            long audioReceived = 0, framesDecoded = 0;
            using var report = JsonDocument.Parse(await _connection.GetStatsJsonAsync());
            foreach (var stat in report.RootElement.EnumerateArray())
            {
                if (stat.TryGetProperty("type", out var type) && type.GetString() == "inbound-rtp" && stat.TryGetProperty("kind", out var kind))
                {
                    if (kind.GetString() == "audio" && stat.TryGetProperty("packetsReceived", out var packets))
                        audioReceived = packets.GetInt64();
                    if (kind.GetString() == "video" && stat.TryGetProperty("framesDecoded", out var frames))
                        framesDecoded = frames.GetInt64();
                }
            }
            return $"{State} audio={audioReceived} video={framesDecoded}";
        }
#endif

        void Enqueue(Func<Task> step) => _work.Writer.TryWrite(step);

        async Task ProcessWorkAsync()
        {
            await foreach (var step in _work.Reader.ReadAllAsync())
            {
                try
                {
                    await step();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // A failed step leaves the connection unable to connect, which ends in the Failed state or a hang-up.
                    System.Diagnostics.Debug.WriteLine($"Call signaling step failed: {e}");
                }
            }
        }
    }
}
