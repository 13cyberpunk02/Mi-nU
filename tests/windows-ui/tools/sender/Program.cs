// The other person in a chat for the checks: signs in as a test user and acts through SignalR, as the app does.
//   sender <server> <user> <password> <chatId> <what>
// where <what> is the text of a message to send, or one of:
//   read:<id>                 marks the chat read up to message <id>
//   reply:<id>:<text>         answers message <id>
//   edit:<id>:<text>          changes the text of one's own message <id>
//   delete:<id>               deletes one's own message <id>
//   react:<id>:<emoji>        reacts to message <id>; react:<id>: takes it back
//   typing:<seconds>          says "typing" every two seconds for that long
//   photo:<path>, file:<path>, voice:<seconds>:<path>, videonote:<seconds>:<path>, avatar:<path>
//   call:<1 for video>:<seconds>, answer:<seconds>, online:<seconds>
// It prints what it did; for a message it sent, its id.
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

var (server, user, password, chatId, text) = (args[0], args[1], args[2], Guid.Parse(args[3]), args[4]);
using var http = new HttpClient();
var response = await http.PostAsJsonAsync($"{server}/api/auth/login", new { userName = user, password });
response.EnsureSuccessStatusCode();
var token = (await response.Content.ReadFromJsonAsync<Auth>())!.Token;

var hub = new HubConnectionBuilder()
    .WithUrl($"{server}/hubs/chat", options => options.AccessTokenProvider = () => Task.FromResult<string?>(token))
    .Build();
await hub.StartAsync();
if (text.StartsWith("reply:"))
{
    var parts = text.Split(':', 3);
    var sent = await hub.InvokeAsync<Sent>("Send", new { chatId, text = parts[2], clientMessageId = Guid.NewGuid(), replyToId = long.Parse(parts[1]) });
    Console.WriteLine($"sent {sent.Id}");
}
else if (text.StartsWith("edit:"))
{
    var parts = text.Split(':', 3);
    await hub.InvokeAsync<Sent>("EditMessage", long.Parse(parts[1]), parts[2]);
    Console.WriteLine("edited");
}
else if (text.StartsWith("delete:"))
{
    await hub.InvokeAsync("DeleteMessage", long.Parse(text[7..]));
    Console.WriteLine("deleted");
}
else if (text.StartsWith("react:"))
{
    var parts = text.Split(':', 3);
    await hub.InvokeAsync<Sent>("React", long.Parse(parts[1]), parts[2].Length > 0 ? parts[2] : null);
    Console.WriteLine("reacted");
}
else if (text.StartsWith("typing:"))
{
    var until = DateTime.UtcNow.AddSeconds(int.Parse(text[7..]));
    while (DateTime.UtcNow < until)
    {
        await hub.InvokeAsync("Typing", chatId);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
    Console.WriteLine("typed");
}
else if (text.StartsWith("avatar:"))
{
    // avatar:<path>: sets the picture as this user's profile picture.
    var path = text[7..];
    using var form = new MultipartFormDataContent();
    var file = new ByteArrayContent(File.ReadAllBytes(path));
    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(path.EndsWith(".png") ? "image/png" : "image/jpeg");
    form.Add(file, "file", Path.GetFileName(path));
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    var response2 = await http.PutAsync($"{server}/api/me/avatar", form);
    response2.EnsureSuccessStatusCode();
    Console.WriteLine($"avatar {await response2.Content.ReadAsStringAsync()}");
}
else if (text.StartsWith("voice:") || text.StartsWith("videonote:"))
{
    // voice:<seconds>:<path>: sends the .m4a as a voice message of that length; videonote: the .mp4 as a round video.
    var parts = text.Split(':', 3);
    var video = text.StartsWith("videonote:");
    using var form = new MultipartFormDataContent();
    var file = new ByteArrayContent(File.ReadAllBytes(parts[2]));
    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(video ? "video/mp4" : "audio/mp4");
    form.Add(file, "file", video ? "video.mp4" : "voice.m4a");
    form.Add(new StringContent(parts[1]), "duration");
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    var uploaded = await http.PostAsync($"{server}/api/chats/{chatId}/files", form);
    uploaded.EnsureSuccessStatusCode();
    var fileId = (await uploaded.Content.ReadFromJsonAsync<Uploaded>())!.Id;
    await hub.InvokeAsync<object>("SendAttachments", chatId, "", Guid.NewGuid(), new[] { fileId });
    Console.WriteLine($"voice {fileId}");
}
else if (text.StartsWith("photo:") || text.StartsWith("file:"))
{
    // photo:<path> or file:<path>: uploads it into the chat and sends it, as the app does.
    var path = text[(text.IndexOf(':') + 1)..];
    using var form = new MultipartFormDataContent();
    var file = new ByteArrayContent(File.ReadAllBytes(path));
    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
        text.StartsWith("file:") ? "application/octet-stream" : path.EndsWith(".png") ? "image/png" : "image/jpeg");
    form.Add(file, "file", Path.GetFileName(path));
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    var uploaded = await http.PostAsync($"{server}/api/chats/{chatId}/files", form);
    uploaded.EnsureSuccessStatusCode();
    var fileId = (await uploaded.Content.ReadFromJsonAsync<Uploaded>())!.Id;
    await hub.InvokeAsync<object>("SendAttachments", chatId, "", Guid.NewGuid(), new[] { fileId });
    Console.WriteLine($"photo {fileId}");
}
else if (text.StartsWith("call:"))
{
    // call:<1 for video>:<seconds>: rings the chat and hangs up after that long, unless someone answers.
    var parts = text.Split(':');
    var started = await hub.InvokeAsync<CallJoin>("StartCall", chatId, parts[1] == "1");
    Console.WriteLine("ringing");
    await Task.Delay(TimeSpan.FromSeconds(int.Parse(parts[2])));
    await hub.InvokeAsync("LeaveCall", started.CallId);
}
else if (text.StartsWith("answer:"))
{
    // answer:<seconds>: waits up to 30 s for a call, joins it, stays that long, then leaves.
    var invited = new TaskCompletionSource<Guid>();
    hub.On<Invite>("CallInvited", invite => invited.TrySetResult(invite.CallId));
    var callId = await invited.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await hub.InvokeAsync<CallJoin>("JoinCall", callId);
    Console.WriteLine("answered");
    await Task.Delay(TimeSpan.FromSeconds(int.Parse(text[7..])));
    await hub.InvokeAsync("LeaveCall", callId);
}
else if (text.StartsWith("online:"))
{
    // Stays connected, so online, for that many seconds.
    Console.WriteLine("online");
    await Task.Delay(TimeSpan.FromSeconds(int.Parse(text[7..])));
}
else if (text.StartsWith("read:"))
{
    await hub.InvokeAsync("MarkRead", chatId, long.Parse(text[5..]));
    Console.WriteLine("read");
}
else
{
    var sent = await hub.InvokeAsync<Sent>("SendMessage", chatId, text, Guid.NewGuid());
    Console.WriteLine($"sent {sent.Id}");
}
await hub.DisposeAsync();

record Auth(string Token);
record Sent(long Id);
record Uploaded(Guid Id);
record CallJoin(Guid CallId);
record Invite(Guid CallId);
