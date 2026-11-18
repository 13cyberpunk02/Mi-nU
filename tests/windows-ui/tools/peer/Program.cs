// A real call participant for tests: waits to be called, joins, talks through the PC's microphone, then leaves.
//   peer <server> <user> <password> <seconds in the call>
using System.Net.Http.Json;
using System.Text.Json;
using Maui.WebRTC;
using Microsoft.AspNetCore.SignalR.Client;

if (args[0] == "devices")
{
    using var devices = new PeerConnectionFactory();
    foreach (var output in devices.AudioOutputs) Console.WriteLine($"output {output.Index}: {output.Name}");
    Console.WriteLine($"set output 0: {devices.SetAudioOutput(devices.AudioOutputs.FirstOrDefault())}, default: {devices.SetAudioOutput(null)}");
    foreach (var camera in CameraCapturer.Devices) Console.WriteLine($"camera: {camera}");
    return;
}
var (server, user, password, seconds) = (args[0], args[1], args[2], int.Parse(args[3]));
using var http = new HttpClient();
var login = await http.PostAsJsonAsync($"{server}/api/auth/login", new { userName = user, password });
login.EnsureSuccessStatusCode();
var token = (await login.Content.ReadFromJsonAsync<Auth>())!.Token;

var hub = new HubConnectionBuilder()
    .WithUrl($"{server}/hubs/chat", options => options.AccessTokenProvider = () => Task.FromResult<string?>(token))
    .Build();
var invited = new TaskCompletionSource<Guid>();
hub.On<Invite>("CallInvited", invite => invited.TrySetResult(invite.CallId));
PeerConnection? connection = null;
var queue = new SemaphoreSlim(1, 1);
hub.On<Signal>("CallSignalReceived", signal => _ = Task.Run(async () =>
{
    await queue.WaitAsync();
    try
    {
        if (signal.Type == "answer")
            await connection!.SetRemoteDescriptionAsync(new SessionDescription("answer", signal.Payload));
        else if (signal.Type == "candidate")
            await connection!.AddIceCandidateAsync(JsonSerializer.Deserialize<IceCandidate>(signal.Payload)!);
    }
    finally { queue.Release(); }
}));
await hub.StartAsync();
Console.WriteLine("waiting for a call");

var callId = await invited.Task.WaitAsync(TimeSpan.FromSeconds(60));
var joined = await hub.InvokeAsync<Join>("JoinCall", callId);
var other = joined.Others[0].ParticipantId;
Task Send(string type, string payload) => hub.InvokeAsync("SendCallSignal", callId, other, type, payload);

using var factory = new PeerConnectionFactory();
using var audio = factory.CreateAudioTrack();
connection = factory.CreatePeerConnection();
connection.IceCandidateGathered += candidate => _ = Send("candidate", JsonSerializer.Serialize(candidate));
connection.ConnectionStateChanged += state => Console.WriteLine($"state {state}");
connection.AddTrack(audio);

await queue.WaitAsync();
try
{
    var offer = await connection.CreateOfferAsync();
    await Send(offer.Type, offer.Sdp);
    await connection.SetLocalDescriptionAsync(offer);
}
finally { queue.Release(); }
Console.WriteLine("joined, talking");

await Task.Delay(TimeSpan.FromSeconds(seconds));
await hub.InvokeAsync("LeaveCall", callId);
connection.Dispose();
await hub.DisposeAsync();
Console.WriteLine("left");

record Auth(string Token);
record Invite(Guid CallId);
record Participant(string ParticipantId);
record Join(Guid CallId, string ParticipantId, List<Participant> Others);
record Signal(Guid CallId, string FromParticipantId, string Type, string Payload);
