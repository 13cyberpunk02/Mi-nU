using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MinU.Contracts;
using MinU.Server;

namespace MinU.Server.Tests;

/// <summary>Runs against a throwaway database in the PostgreSQL from docker-compose.yml.</summary>
public class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    readonly string _database = "minu_test_" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"Host=127.0.0.1;Port=5433;Database={_database};Username=minu;Password=minu_dev");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:AttemptsPerMinute", "1000");
        builder.UseSetting("Turn:Secret", TurnSecret);
        builder.UseSetting("Turn:Urls:0", "turn:turn.example.com:3478?transport=udp");
        builder.UseSetting("Turn:Urls:1", "turns:turn.example.com:5349?transport=tcp");
        // The SeaweedFS from server/docker-compose.yml; a bucket of its own per run.
        builder.UseSetting("Storage:ServiceUrl", "http://127.0.0.1:8333");
        builder.UseSetting("Storage:PublicUrl", "http://127.0.0.1:8333");
        builder.UseSetting("Storage:AccessKey", "minu_dev");
        builder.UseSetting("Storage:SecretKey", "minu_dev_secret");
        builder.UseSetting("Storage:Bucket", "minu-test-" + _database[^12..]);
    }

    public const string TurnSecret = "test-turn-secret";

    /// <summary>Stands in for Firebase: records what would have been sent.</summary>
    public FakePushSender Push { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPushSender>();
            services.AddSingleton<IPushSender>(Push);
        });
        return base.CreateHost(builder);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        using (var scope = Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        await base.DisposeAsync();
    }

    public async Task<Client> RegisterAsync(string name)
    {
        var http = CreateClient();
        var response = await http.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(name + "_" + Guid.NewGuid().ToString("N")[..8], name, "correct-password"));
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(auth.Token);
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.WebSocketFactory = async (context, token) =>
                {
                    var uri = new UriBuilder(context.Uri) { Query = context.Uri.Query.TrimStart('?') + "&access_token=" + auth.Token }.Uri;
                    return await Server.CreateWebSocketClient().ConnectAsync(uri, token);
                };
            })
            .Build();
        await hub.StartAsync();
        return new Client(http, hub, auth.User, auth.Token);
    }
}

public sealed class FakePushSender : IPushSender
{
    readonly List<(string Token, IReadOnlyDictionary<string, string> Data)> _sent = [];

    /// <summary>Tokens Firebase would reject, as for an uninstalled app.</summary>
    public HashSet<string> Invalid { get; } = [];

    public Task<PushResult> SendAsync(string token, IReadOnlyDictionary<string, string> data, TimeSpan timeToLive, CancellationToken cancel)
    {
        lock (_sent)
            _sent.Add((token, data));
        return Task.FromResult(Invalid.Contains(token) ? PushResult.InvalidToken : PushResult.Sent);
    }

    /// <summary>Waits for a push of the given type to the token: pushes go out in the background.</summary>
    public async Task<IReadOnlyDictionary<string, string>> NextAsync(string token, string type, int skip = 0)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (_sent)
            {
                var match = _sent.Where(x => x.Token == token && x.Data[PushKeys.Type] == type).Skip(skip).FirstOrDefault();
                if (match.Data is not null)
                    return match.Data;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"No {type} push to {token}");
    }

    public int CountOf(string token, string type)
    {
        lock (_sent)
            return _sent.Count(x => x.Token == token && x.Data[PushKeys.Type] == type);
    }

    public int CountTo(string token)
    {
        lock (_sent)
            return _sent.Count(x => x.Token == token);
    }
}

public sealed record Client(HttpClient Http, HubConnection Hub, UserDto User, string Token)
{
    public async Task<string> RegisterDeviceAsync()
    {
        var token = "fcm-" + Guid.NewGuid().ToString("N");
        var response = await Http.PutAsJsonAsync("/api/devices", new RegisterDeviceRequest(token, "android"));
        response.EnsureSuccessStatusCode();
        return token;
    }

    public async Task<ChatDto> DirectChatWithAsync(Client other)
    {
        var response = await Http.PostAsJsonAsync("/api/chats/direct", new CreateDirectChatRequest(other.User.Id));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatDto>())!;
    }

    public async Task<ChatDto> GroupWithAsync(string title, params Client[] others)
    {
        var response = await Http.PostAsJsonAsync("/api/chats/group", new CreateGroupChatRequest(title, others.Select(x => x.User.Id).ToList()));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatDto>())!;
    }

    public Task<MessageDto> SendAsync(Guid chatId, string text, Guid? clientId = null) =>
        Hub.InvokeAsync<MessageDto>("SendMessage", chatId, text, clientId ?? Guid.NewGuid());

    public Task<T> Next<T>(string hubMethod)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;
        subscription = Hub.On<T>(hubMethod, value =>
        {
            subscription?.Dispose();
            result.TrySetResult(value);
        });
        return result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>The next call of a hub method with two arguments.</summary>
    public Task<(T1, T2)> Next<T1, T2>(string hubMethod)
    {
        var result = new TaskCompletionSource<(T1, T2)>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;
        subscription = Hub.On<T1, T2>(hubMethod, (first, second) =>
        {
            subscription?.Dispose();
            result.TrySetResult((first, second));
        });
        return result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task<MessageDto> ReplyAsync(Guid chatId, string text, long replyToId) =>
        Hub.InvokeAsync<MessageDto>("Send", new SendMessageRequest(chatId, text, Guid.NewGuid(), ReplyToId: replyToId));
}

public class ServerTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Login_accepts_the_right_password_only()
    {
        var alice = await server.RegisterAsync("alice");
        var http = server.CreateClient();

        var right = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(alice.User.UserName.ToUpperInvariant(), "correct-password"));
        var wrong = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(alice.User.UserName, "wrong-password"));
        var unknown = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody_here", "correct-password"));

        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    async Task<AuthResponse> LoginAsync(Client client)
    {
        var response = await server.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(client.User.UserName, "correct-password"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    async Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        await server.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken));

    [Fact]
    public async Task Access_token_is_short_lived_and_refresh_token_long_lived()
    {
        var auth = await LoginAsync(await server.RegisterAsync("alice"));
        Assert.InRange(auth.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(55), DateTimeOffset.UtcNow.AddMinutes(65));
        Assert.InRange(auth.RefreshExpiresAt, DateTimeOffset.UtcNow.AddDays(59), DateTimeOffset.UtcNow.AddDays(61));
    }

    [Fact]
    public async Task Refresh_gives_a_working_pair_and_each_refresh_token_works_once()
    {
        var first = await LoginAsync(await server.RegisterAsync("alice"));

        var response = await RefreshAsync(first.RefreshToken);
        response.EnsureSuccessStatusCode();
        var second = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var http = server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/chats")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(second.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task A_copied_refresh_token_used_again_ends_every_sign_in()
    {
        var alice = await server.RegisterAsync("alice");
        var stolen = await LoginAsync(alice);
        var otherDevice = await LoginAsync(alice);
        var owner = (await (await RefreshAsync(stolen.RefreshToken)).Content.ReadFromJsonAsync<AuthResponse>())!;

        // The thief tries the token the owner has already swapped.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(stolen.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(owner.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(otherDevice.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Sign_out_ends_only_that_device()
    {
        var alice = await server.RegisterAsync("alice");
        var phone = await LoginAsync(alice);
        var computer = await LoginAsync(alice);

        (await server.CreateClient().PostAsJsonAsync("/api/auth/logout", new RefreshRequest(phone.RefreshToken))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(phone.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(computer.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync("made-up-token")).StatusCode);
    }

    [Fact]
    public async Task Username_must_be_unique_ignoring_case()
    {
        var alice = await server.RegisterAsync("alice");
        var response = await server.CreateClient().PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(alice.User.UserName.ToUpperInvariant(), "Other", "correct-password"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Api_and_hub_reject_anonymous_clients()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateClient().GetAsync("/api/chats")).StatusCode);

        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.Server.BaseAddress, "/hubs/chat"), options => options.HttpMessageHandlerFactory = _ => server.Server.CreateHandler())
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task Ice_servers_come_with_credentials_coturn_accepts()
    {
        var alice = await server.RegisterAsync("alice");
        var ice = (await alice.Http.GetFromJsonAsync<IceServersDto>("/api/ice-servers"))!;

        Assert.Equal(2, ice.Urls.Count);
        var parts = ice.Username.Split(':');
        Assert.Equal(alice.User.Id.ToString("N"), parts[1]);
        Assert.InRange(DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[0])), DateTimeOffset.UtcNow.AddHours(11), DateTimeOffset.UtcNow.AddHours(13));
        // The check coturn does with use-auth-secret.
        var expected = Convert.ToBase64String(System.Security.Cryptography.HMACSHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(ServerFixture.TurnSecret), System.Text.Encoding.UTF8.GetBytes(ice.Username)));
        Assert.Equal(expected, ice.Credential);

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateClient().GetAsync("/api/ice-servers")).StatusCode);
    }

    [Fact]
    public async Task Message_pushes_to_other_members_with_sender_and_group()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var carol = await server.RegisterAsync("carol");
        var aliceDevice = await alice.RegisterDeviceAsync();
        var bobDevice = await bob.RegisterDeviceAsync();
        var carolDevice = await carol.RegisterDeviceAsync();

        var direct = await alice.DirectChatWithAsync(bob);
        await alice.SendAsync(direct.Id, "hello bob");
        var push = await server.Push.NextAsync(bobDevice, PushKeys.TypeMessage);
        Assert.Equal(("hello bob", "alice", "", direct.Id.ToString()), (push[PushKeys.Text], push[PushKeys.Sender], push[PushKeys.ChatTitle], push[PushKeys.ChatId]));

        var group = await alice.GroupWithAsync("team", bob, carol);
        await alice.SendAsync(group.Id, new string('x', Limits.PushTextMax + 50));
        var groupPush = await server.Push.NextAsync(carolDevice, PushKeys.TypeMessage);
        Assert.Equal("team", groupPush[PushKeys.ChatTitle]);
        Assert.Equal(Limits.PushTextMax + 1, groupPush[PushKeys.Text].Length);

        // The sender's own phone stays quiet.
        await Task.Delay(300);
        Assert.Equal(0, server.Push.CountTo(aliceDevice));
    }

    [Fact]
    public async Task Call_pushes_ring_and_stop()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var bobDevice = await bob.RegisterDeviceAsync();
        var chat = await alice.DirectChatWithAsync(bob);

        var started = await alice.Hub.InvokeAsync<CallJoinResult>("StartCall", chat.Id, true);
        var ring = await server.Push.NextAsync(bobDevice, PushKeys.TypeCall);
        Assert.Equal((started.CallId.ToString(), "alice", "1"), (ring[PushKeys.CallId], ring[PushKeys.Sender], ring[PushKeys.Video]));

        await alice.Hub.InvokeAsync("LeaveCall", started.CallId);
        var ended = await server.Push.NextAsync(bobDevice, PushKeys.TypeCallEnded);
        Assert.Equal(started.CallId.ToString(), ended[PushKeys.CallId]);
    }

    [Fact]
    public async Task Device_belongs_to_whoever_signed_in_last_and_leaves_on_sign_out()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var carol = await server.RegisterAsync("carol");
        var token = await alice.RegisterDeviceAsync();
        (await bob.Http.PutAsJsonAsync("/api/devices", new RegisterDeviceRequest(token, "android"))).EnsureSuccessStatusCode();

        // The phone is Bob's now: Carol's message to Alice must not show on it.
        await carol.SendAsync((await carol.DirectChatWithAsync(alice)).Id, "for alice");
        await carol.SendAsync((await carol.DirectChatWithAsync(bob)).Id, "for bob");
        Assert.Equal("for bob", (await server.Push.NextAsync(token, PushKeys.TypeMessage))[PushKeys.Text]);

        (await bob.Http.DeleteAsync($"/api/devices?token={token}")).EnsureSuccessStatusCode();
        await carol.SendAsync((await carol.DirectChatWithAsync(bob)).Id, "after sign-out");
        await Task.Delay(300);
        Assert.Equal(1, server.Push.CountTo(token));
    }

    [Fact]
    public async Task Rejected_tokens_are_forgotten()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var token = await bob.RegisterDeviceAsync();
        server.Push.Invalid.Add(token);
        var chat = await alice.DirectChatWithAsync(bob);

        await alice.SendAsync(chat.Id, "first");
        await server.Push.NextAsync(token, PushKeys.TypeMessage);
        await Task.Delay(300);
        await alice.SendAsync(chat.Id, "second");
        await Task.Delay(300);
        Assert.Equal(1, server.Push.CountTo(token));
    }

    [Fact]
    public async Task Declining_from_a_notification_tells_the_caller()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var mallory = await server.RegisterAsync("mallory");
        var chat = await alice.DirectChatWithAsync(bob);
        var started = await alice.Hub.InvokeAsync<CallJoinResult>("StartCall", chat.Id, false);

        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.PostAsync($"/api/calls/{started.CallId}/decline", null)).StatusCode);

        var declined = new TaskCompletionSource<UserDto>();
        using var _ = alice.Hub.On<Guid, UserDto>("CallDeclined", (_, user) => declined.TrySetResult(user));
        (await bob.Http.PostAsync($"/api/calls/{started.CallId}/decline", null)).EnsureSuccessStatusCode();
        Assert.Equal(bob.User.Id, (await declined.Task.WaitAsync(TimeSpan.FromSeconds(10))).Id);
    }

    [Fact]
    public async Task Unknown_device_platforms_are_refused()
    {
        var alice = await server.RegisterAsync("alice");
        var response = await alice.Http.PutAsJsonAsync("/api/devices", new RegisterDeviceRequest("token", "ios"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Token_of_a_deleted_account_is_rejected()
    {
        var alice = await server.RegisterAsync("alice");
        Assert.Equal(HttpStatusCode.OK, (await alice.Http.GetAsync("/api/chats")).StatusCode);

        using (var scope = server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(x => x.Id == alice.User.Id).ExecuteDeleteAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.Http.GetAsync("/api/chats")).StatusCode);
    }

    [Fact]
    public async Task Direct_chat_delivers_messages_and_keeps_history()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");

        var created = bob.Next<ChatDto>("ChatCreated");
        var chat = await alice.DirectChatWithAsync(bob);
        Assert.Equal("bob", chat.Title);
        Assert.Equal("alice", (await created).Title);

        var received = bob.Next<MessageDto>("MessageReceived");
        await alice.SendAsync(chat.Id, "  hello  ");
        Assert.Equal("hello", (await received).Text);

        var history = await bob.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages");
        Assert.Equal("hello", Assert.Single(history!).Text);

        var chats = await bob.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats");
        Assert.Equal("hello", chats!.Single(x => x.Id == chat.Id).LastMessage!.Text);
    }

    [Fact]
    public async Task Opening_a_direct_chat_twice_returns_the_same_chat_from_both_sides()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var first = await alice.DirectChatWithAsync(bob);
        var second = await bob.DirectChatWithAsync(alice);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Resending_with_the_same_client_id_does_not_duplicate()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);
        var clientId = Guid.NewGuid();

        var first = await alice.SendAsync(chat.Id, "once", clientId);
        var second = await alice.SendAsync(chat.Id, "once", clientId);

        Assert.Equal(first.Id, second.Id);
        Assert.Single((await alice.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages"))!);
    }

    [Fact]
    public async Task History_pages_backwards_in_order()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);
        for (var i = 1; i <= 5; i++)
            await alice.SendAsync(chat.Id, i.ToString());

        var latest = (await alice.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages?limit=2"))!;
        var older = (await alice.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages?limit=2&before={latest[0].Id}"))!;

        Assert.Equal(["4", "5"], latest.Select(x => x.Text));
        Assert.Equal(["2", "3"], older.Select(x => x.Text));
    }

    [Fact]
    public async Task Outsiders_cannot_read_or_write_a_chat()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var mallory = await server.RegisterAsync("mallory");
        var chat = await alice.DirectChatWithAsync(bob);

        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.GetAsync($"/api/chats/{chat.Id}/messages")).StatusCode);
        await Assert.ThrowsAsync<HubException>(() => mallory.SendAsync(chat.Id, "hi"));
        await Assert.ThrowsAsync<HubException>(() => mallory.Hub.InvokeAsync<CallJoinResult>("StartCall", chat.Id, true));
    }

    [Fact]
    public async Task Group_message_reaches_every_member()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var carol = await server.RegisterAsync("carol");

        var carolCreated = carol.Next<ChatDto>("ChatCreated");
        var group = await alice.GroupWithAsync("team", bob, carol);
        Assert.Equal("team", (await carolCreated).Title);
        Assert.Equal(3, group.Members.Count);

        var bobGot = bob.Next<MessageDto>("MessageReceived");
        var carolGot = carol.Next<MessageDto>("MessageReceived");
        await alice.SendAsync(group.Id, "hi all");
        Assert.Equal("hi all", (await bobGot).Text);
        Assert.Equal("hi all", (await carolGot).Text);
    }

    [Fact]
    public async Task Unread_counts_others_messages_until_read_and_tells_the_sender()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);
        await alice.SendAsync(chat.Id, "one");
        await alice.SendAsync(chat.Id, "two");
        var last = await alice.SendAsync(chat.Id, "three");

        async Task<ChatDto> ChatOf(Client client) =>
            (await client.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!.Single(x => x.Id == chat.Id);

        Assert.Equal(3, (await ChatOf(bob)).UnreadCount);
        Assert.Equal(0, (await ChatOf(alice)).UnreadCount);
        Assert.Equal(0, (await ChatOf(alice)).ReadUpTo);

        var aliceSaw = alice.Next<ReadReceipt>("ChatRead");
        var bobSaw = bob.Next<ReadReceipt>("ChatRead");
        // An id past the end is clamped to the newest message.
        await bob.Hub.InvokeAsync("MarkRead", chat.Id, last.Id + 1000);
        Assert.Equal(new ReadReceipt(chat.Id, bob.User.Id, last.Id), await aliceSaw);
        Assert.Equal(last.Id, (await bobSaw).MessageId);

        Assert.Equal(0, (await ChatOf(bob)).UnreadCount);
        Assert.Equal(last.Id, (await ChatOf(alice)).ReadUpTo);

        // The mark never moves back.
        await bob.Hub.InvokeAsync("MarkRead", chat.Id, last.Id - 1);
        Assert.Equal(last.Id, (await ChatOf(alice)).ReadUpTo);

        // Replying means having read the chat, and one's own message is never unread.
        await alice.SendAsync(chat.Id, "four");
        var reply = await bob.SendAsync(chat.Id, "reply");
        Assert.Equal(0, (await ChatOf(bob)).UnreadCount);
        Assert.Equal(1, (await ChatOf(alice)).UnreadCount);
        Assert.Equal(reply.Id, (await ChatOf(alice)).ReadUpTo);
    }

    [Fact]
    public async Task Group_can_be_renamed_and_grown_by_any_member()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var carol = await server.RegisterAsync("carol");
        var group = await alice.GroupWithAsync("team", bob);
        Assert.Equal(alice.User.Id, group.OwnerId);
        await alice.SendAsync(group.Id, "before carol");

        var aliceUpdated = alice.Next<ChatDto>("ChatUpdated");
        var aliceLine = alice.Next<MessageDto>("MessageReceived");
        var renamed = await bob.Http.PutAsJsonAsync($"/api/chats/{group.Id}/title", new RenameGroupRequest(" crew "));
        renamed.EnsureSuccessStatusCode();
        Assert.Equal("crew", (await aliceUpdated).Title);
        var line = await aliceLine;
        Assert.Equal((MessageKind.System, "bob переименовал(а) группу в «crew»"), (line.Kind, line.Text));

        var carolCreated = carol.Next<ChatDto>("ChatCreated");
        var bobUpdated = bob.Next<ChatDto>("ChatUpdated");
        var added = await bob.Http.PostAsJsonAsync($"/api/chats/{group.Id}/members", new AddMembersRequest([carol.User.Id, bob.User.Id]));
        added.EnsureSuccessStatusCode();
        Assert.Equal(3, (await carolCreated).Members.Count);
        Assert.Equal(3, (await bobUpdated).Members.Count);

        // Carol takes part from now on: only the line about her joining is new to her, not the history before.
        var chats = await carol.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats");
        var carolsView = chats!.Single(x => x.Id == group.Id);
        Assert.Equal(1, carolsView.UnreadCount);
        Assert.Equal("bob добавил(а) carol", carolsView.LastMessage!.Text);
        await carol.SendAsync(group.Id, "hi, I'm new");

        // A direct chat has no title of its own to change.
        var direct = await alice.DirectChatWithAsync(bob);
        var refused = await alice.Http.PutAsJsonAsync($"/api/chats/{direct.Id}/title", new RenameGroupRequest("x"));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_removes_others_and_ownership_passes_on_when_the_owner_leaves()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var carol = await server.RegisterAsync("carol");
        var group = await alice.GroupWithAsync("team", bob, carol);

        var refused = await bob.Http.DeleteAsync($"/api/chats/{group.Id}/members/{carol.User.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var carolRemoved = carol.Next<Guid>("ChatRemoved");
        var bobLine = bob.Next<MessageDto>("MessageReceived");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.DeleteAsync($"/api/chats/{group.Id}/members/{carol.User.Id}")).StatusCode);
        Assert.Equal(group.Id, await carolRemoved);
        Assert.Equal("alice удалил(а) carol", (await bobLine).Text);
        await Assert.ThrowsAsync<HubException>(() => carol.SendAsync(group.Id, "still here?"));
        Assert.Equal(HttpStatusCode.NotFound, (await carol.Http.GetAsync($"/api/chats/{group.Id}/messages")).StatusCode);

        var bobUpdated = bob.Next<ChatDto>("ChatUpdated");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.DeleteAsync($"/api/chats/{group.Id}/members/{alice.User.Id}")).StatusCode);
        var afterLeaving = await bobUpdated;
        Assert.Equal(bob.User.Id, afterLeaving.OwnerId);
        Assert.Equal("alice вышел(а) из группы", afterLeaving.LastMessage!.Text);

        // The last one out takes the group with them.
        var bobRemoved = bob.Next<Guid>("ChatRemoved");
        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.DeleteAsync($"/api/chats/{group.Id}/members/{bob.User.Id}")).StatusCode);
        Assert.Equal(group.Id, await bobRemoved);
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Chats.AnyAsync(x => x.Id == group.Id));
        Assert.False(await db.Messages.AnyAsync(x => x.ChatId == group.Id));
    }

    // Downloads go straight to storage, as an app would, without the server's test handler.
    static readonly HttpClient Storage = new();

    // The first bytes of a PNG, which is all the server looks at.
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3, 4];

    static async Task<FileDto> UploadAsync(Client client, Guid chatId, byte[] bytes, string name, string contentType,
        int? width = null, int? height = null, int? duration = null, string? waveform = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", name);
        if (width is { } w)
            form.Add(new StringContent(w.ToString()), "width");
        if (height is { } h)
            form.Add(new StringContent(h.ToString()), "height");
        if (duration is { } d)
            form.Add(new StringContent(d.ToString()), "duration");
        if (waveform is not null)
            form.Add(new StringContent(waveform), "waveform");
        var response = await client.Http.PostAsync($"/api/chats/{chatId}/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileDto>())!;
    }

    [Fact]
    public async Task Chat_members_get_a_working_link_and_nobody_else_does()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var mallory = await server.RegisterAsync("mallory");
        var chat = await alice.DirectChatWithAsync(bob);
        var bytes = Png.Concat(Enumerable.Range(0, 5000 - Png.Length).Select(i => (byte)i)).ToArray();

        var uploaded = await UploadAsync(alice, chat.Id, bytes, "photo.png", "image/png");
        Assert.Equal(("photo.png", "image/png", 5000L), (uploaded.Name, uploaded.ContentType, uploaded.Size));

        var link = (await bob.Http.GetFromJsonAsync<FileLinkDto>($"/api/files/{uploaded.Id}/link"))!;
        Assert.InRange(link.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));
        using (var download = await Storage.GetAsync(link.Url))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        }

        // Outside the chat neither the link nor an upload into it.
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.GetAsync($"/api/files/{uploaded.Id}/link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.GetAsync($"/api/files/{Guid.NewGuid()}/link")).StatusCode);
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "x.bin" } };
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.PostAsync($"/api/chats/{chat.Id}/files", form)).StatusCode);

        // Without a signature, or with the link pointed at another file, storage refuses.
        Assert.Equal(HttpStatusCode.Forbidden, (await Storage.GetAsync(link.Url.GetLeftPart(UriPartial.Path))).StatusCode);
        var other = await UploadAsync(alice, chat.Id, [9, 9, 9], "other.png", "image/png");
        var forged = link.Url.ToString().Replace(uploaded.Id.ToString("N"), other.Id.ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Storage.GetAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task Links_expire_and_unknown_types_only_download()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);

        // A page must never open from storage: HTML is kept as a download, under its own name.
        var page = await UploadAsync(alice, chat.Id, "<script>alert(1)</script>"u8.ToArray(), "../../отчёт.html", "text/html");
        Assert.Equal(("отчёт.html", "application/octet-stream"), (page.Name, page.ContentType));
        var link = (await alice.Http.GetFromJsonAsync<FileLinkDto>($"/api/files/{page.Id}/link"))!;
        using (var download = await Storage.GetAsync(link.Url))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("отчёт.html", download.Content.Headers.ContentDisposition?.FileNameStar);
        }

        // A link only works for as long as it was made for.
        var storage = server.Services.GetRequiredService<IFileStorage>();
        var shortLived = storage.LinkFor(page.Id.ToString("N"), TimeSpan.FromSeconds(1), null);
        Assert.Equal(HttpStatusCode.OK, (await Storage.GetAsync(shortLived)).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(HttpStatusCode.Forbidden, (await Storage.GetAsync(shortLived)).StatusCode);
    }

    [Fact]
    public async Task Photos_are_sent_in_a_message_with_their_size()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var bobDevice = await bob.RegisterDeviceAsync();
        var chat = await alice.DirectChatWithAsync(bob);

        var first = await UploadAsync(alice, chat.Id, Png, "a.png", "image/png", 1200, 800);
        var second = await UploadAsync(alice, chat.Id, Png, "b.png", "image/png", 600, 900);
        Assert.Equal((1200, 800), (first.Width, first.Height));
        // Claiming to be a picture isn't enough: these bytes are no image, so they only download, without a size.
        var fake = await UploadAsync(alice, chat.Id, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13], "x.png", "image/png", 10, 10);
        Assert.Equal(("application/octet-stream", (int?)null), (fake.ContentType, fake.Width));

        var bobGot = bob.Next<MessageDto>("MessageReceived");
        var clientId = Guid.NewGuid();
        var sent = await alice.Hub.InvokeAsync<MessageDto>("SendAttachments", chat.Id, " look ", clientId, new[] { first.Id, second.Id });
        var received = await bobGot;
        Assert.Equal("look", received.Text);
        Assert.Equal([first.Id, second.Id], received.Attachments!.Select(file => file.Id));
        Assert.Equal("Фото: 2 · look", received.Summary);

        // A resend after a lost connection gives the same message, not a second one.
        var again = await alice.Hub.InvokeAsync<MessageDto>("SendAttachments", chat.Id, "look", clientId, new[] { first.Id, second.Id });
        Assert.Equal(sent.Id, again.Id);

        var history = (await bob.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages"))!;
        Assert.Equal(2, Assert.Single(history).Attachments!.Count);
        var chats = (await bob.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!;
        Assert.Equal("Фото: 2 · look", chats.Single(x => x.Id == chat.Id).LastMessage!.Summary);
        Assert.Equal("Фото: 2 · look", (await server.Push.NextAsync(bobDevice, PushKeys.TypeMessage))[PushKeys.Text]);
    }

    [Fact]
    public async Task Only_your_own_unsent_files_from_this_chat_can_be_sent()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var carol = await server.RegisterAsync("carol");
        var chat = await alice.DirectChatWithAsync(bob);
        var other = await alice.DirectChatWithAsync(carol);

        var alices = await UploadAsync(alice, chat.Id, Png, "a.png", "image/png");
        var elsewhere = await UploadAsync(alice, other.Id, Png, "b.png", "image/png");

        Task Send(Client from, Guid chatId, params Guid[] files) =>
            from.Hub.InvokeAsync<MessageDto>("SendAttachments", chatId, "", Guid.NewGuid(), files);

        await Assert.ThrowsAsync<HubException>(() => Send(bob, chat.Id, alices.Id));
        await Assert.ThrowsAsync<HubException>(() => Send(alice, chat.Id, elsewhere.Id));
        await Assert.ThrowsAsync<HubException>(() => Send(alice, chat.Id));
        await Assert.ThrowsAsync<HubException>(() => Send(alice, chat.Id, alices.Id, Guid.NewGuid()));
        await Send(alice, chat.Id, alices.Id);
        // Sent once; a second message can't take it again.
        await Assert.ThrowsAsync<HubException>(() => Send(alice, chat.Id, alices.Id));
        // A refused send claims nothing: the file from the other chat is still free there.
        await Send(alice, other.Id, elsewhere.Id);
    }

    [Fact]
    public async Task Files_never_sent_are_removed_after_a_day_and_sent_ones_stay()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);
        var sent = await UploadAsync(alice, chat.Id, Png, "sent.png", "image/png");
        await alice.Hub.InvokeAsync<MessageDto>("SendAttachments", chat.Id, "", Guid.NewGuid(), new[] { sent.Id });
        var abandoned = await UploadAsync(alice, chat.Id, Png, "abandoned.png", "image/png");
        var fresh = await UploadAsync(alice, chat.Id, Png, "fresh.png", "image/png");

        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Uploaded two days ago and never sent, as when the app closed halfway.
            await db.Files.Where(x => x.Id == abandoned.Id || x.Id == sent.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)));
        }

        var cleanup = server.Services.GetRequiredService<FileCleanup>();
        Assert.True(await cleanup.RemoveUnsentAsync(DateTimeOffset.UtcNow - FileCleanup.UnsentLifetime, CancellationToken.None) >= 1);

        using var check = server.Services.CreateScope();
        var left = await check.ServiceProvider.GetRequiredService<AppDbContext>().Files
            .Where(x => x.Id == sent.Id || x.Id == abandoned.Id || x.Id == fresh.Id).Select(x => x.Id).ToListAsync();
        Assert.Equal(new HashSet<Guid> { sent.Id, fresh.Id }, left.ToHashSet());
        var storage = server.Services.GetRequiredService<IFileStorage>();
        Assert.Equal(HttpStatusCode.NotFound, (await Storage.GetAsync(storage.LinkFor(abandoned.Id.ToString("N"), TimeSpan.FromMinutes(1), null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Storage.GetAsync(storage.LinkFor(sent.Id.ToString("N"), TimeSpan.FromMinutes(1), null))).StatusCode);
    }

    static async Task<HttpResponseMessage> PutAvatarAsync(Client client, byte[] bytes, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "me.png");
        return await client.Http.PutAsync("/api/me/avatar", form);
    }

    [Fact]
    public async Task A_new_picture_and_name_reach_contacts_and_the_old_picture_goes()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var stranger = await server.RegisterAsync("stranger");
        await alice.DirectChatWithAsync(bob);

        var bobSaw = bob.Next<UserDto>("UserUpdated");
        var response = await PutAvatarAsync(alice, Png, "image/png");
        response.EnsureSuccessStatusCode();
        var first = (await response.Content.ReadFromJsonAsync<UserDto>())!;
        Assert.NotNull(first.AvatarId);
        Assert.Equal(first.AvatarId, (await bobSaw).AvatarId);

        // Shown wherever its owner is, so anyone signed in may fetch it, a stranger from a search included.
        var link = (await stranger.Http.GetFromJsonAsync<FileLinkDto>($"/api/files/{first.AvatarId}/link"))!;
        Assert.Equal(HttpStatusCode.OK, (await Storage.GetAsync(link.Url)).StatusCode);

        var second = (await (await PutAvatarAsync(alice, Png, "image/png")).Content.ReadFromJsonAsync<UserDto>())!;
        Assert.NotEqual(first.AvatarId, second.AvatarId);
        // The replaced one is gone, link and bytes.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/files/{first.AvatarId}/link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Storage.GetAsync(link.Url)).StatusCode);

        // The daily cleanup of unsent files leaves profile pictures alone.
        using (var scope = server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Files.Where(x => x.Id == second.AvatarId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-5)));
        await server.Services.GetRequiredService<FileCleanup>().RemoveUnsentAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await stranger.Http.GetAsync($"/api/files/{second.AvatarId}/link")).StatusCode);

        var renamed = bob.Next<UserDto>("UserUpdated");
        (await alice.Http.PutAsJsonAsync("/api/me", new UpdateProfileRequest("  Alice Smith "))).EnsureSuccessStatusCode();
        Assert.Equal("Alice Smith", (await renamed).DisplayName);
        var chats = (await bob.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!;
        Assert.Equal("Alice Smith", chats.Single().Title);
        Assert.Equal(second.AvatarId, chats.Single().Members.Single(x => x.Id == alice.User.Id).AvatarId);

        var removed = bob.Next<UserDto>("UserUpdated");
        (await alice.Http.DeleteAsync("/api/me/avatar")).EnsureSuccessStatusCode();
        Assert.Null((await removed).AvatarId);
    }

    [Fact]
    public async Task Only_real_pictures_become_profile_pictures()
    {
        var alice = await server.RegisterAsync("alice");
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAvatarAsync(alice, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAvatarAsync(alice, "<svg/>"u8.ToArray(), "image/svg+xml")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PutAsJsonAsync("/api/me", new UpdateProfileRequest("   "))).StatusCode);
    }

    [Fact]
    public async Task A_voice_message_keeps_its_length_and_reads_as_one()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);

        var voice = await UploadAsync(alice, chat.Id, [0, 0, 0, 0x18, .. "ftypM4A "u8.ToArray(), 1, 2, 3], "voice.m4a", "audio/mp4", duration: 7,
            waveform: Convert.ToBase64String([0, 5, 31, 12]));
        Assert.Equal(("audio/mp4", (int?)7), (voice.ContentType, voice.DurationSeconds));
        Assert.True(voice.IsVoice);
        // Only sound has a length; a length on anything else is dropped.
        var file = await UploadAsync(alice, chat.Id, [1, 2, 3], "a.bin", "application/zip", duration: 7);
        Assert.Null(file.DurationSeconds);
        // A round video message keeps its length too, and reads as one.
        var note = await UploadAsync(alice, chat.Id, [0, 0, 0, 0x18, .. "ftypisom"u8.ToArray(), 1, 2, 3], "video.mp4", "video/mp4", duration: 5);
        Assert.True(note.IsVideoNote);
        Assert.Equal("Видеосообщение", new MessageDto(1, chat.Id, alice.User, "", DateTimeOffset.UtcNow, Guid.NewGuid(), Attachments: [note]).Summary);
        Assert.Equal(Convert.ToBase64String([0, 5, 31, 12]), voice.Waveform);
        // Levels above 31, or no base64 at all, are not a waveform.
        Assert.Null((await UploadAsync(alice, chat.Id, [1], "v.m4a", "audio/mp4", duration: 1, waveform: Convert.ToBase64String([40]))).Waveform);
        Assert.Null((await UploadAsync(alice, chat.Id, [1], "v.m4a", "audio/mp4", duration: 1, waveform: "<script>")).Waveform);

        var bobGot = bob.Next<MessageDto>("MessageReceived");
        await alice.Hub.InvokeAsync<MessageDto>("SendAttachments", chat.Id, "", Guid.NewGuid(), new[] { voice.Id });
        var received = await bobGot;
        Assert.Equal("Голосовое сообщение", received.Summary);
        Assert.Equal(7, Assert.Single(received.Attachments!).DurationSeconds);
    }

    static async Task<HttpResponseMessage> PutGroupAvatarAsync(Client client, Guid chatId, byte[] bytes, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "group.png");
        return await client.Http.PutAsync($"/api/chats/{chatId}/avatar", form);
    }

    [Fact]
    public async Task A_group_picture_reaches_members_only_and_goes_with_the_group()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var stranger = await server.RegisterAsync("stranger");
        var group = await alice.GroupWithAsync("team", bob);
        Assert.Equal(HttpStatusCode.NotFound, (await PutGroupAvatarAsync(stranger, group.Id, Png, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutGroupAvatarAsync(bob, group.Id, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], "image/png")).StatusCode);

        // Any member may change it, as with the title; everyone hears of it.
        var aliceSaw = alice.Next<ChatDto>("ChatUpdated");
        var line = alice.Next<MessageDto>("MessageReceived");
        var response = await PutGroupAvatarAsync(bob, group.Id, Png, "image/png");
        response.EnsureSuccessStatusCode();
        var first = (await response.Content.ReadFromJsonAsync<ChatDto>())!.AvatarId;
        Assert.NotNull(first);
        Assert.Equal(first, (await aliceSaw).AvatarId);
        Assert.Contains("изменил(а) фото группы", (await line).Text);

        Assert.Equal(HttpStatusCode.OK, (await alice.Http.GetAsync($"/api/files/{first}/link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/files/{first}/link")).StatusCode);

        // A new one replaces the old one, bytes and all; the daily cleanup leaves the current one alone.
        var second = (await (await PutGroupAvatarAsync(alice, group.Id, Png, "image/png")).Content.ReadFromJsonAsync<ChatDto>())!.AvatarId;
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.GetAsync($"/api/files/{first}/link")).StatusCode);
        using (var scope = server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Files.Where(x => x.Id == second)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-5)));
        await server.Services.GetRequiredService<FileCleanup>().RemoveUnsentAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await bob.Http.GetAsync($"/api/files/{second}/link")).StatusCode);
        var chats = (await bob.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats"))!;
        Assert.Equal(second, chats.Single().AvatarId);

        var removed = bob.Next<ChatDto>("ChatUpdated");
        (await alice.Http.DeleteAsync($"/api/chats/{group.Id}/avatar")).EnsureSuccessStatusCode();
        Assert.Null((await removed).AvatarId);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Http.GetAsync($"/api/files/{second}/link")).StatusCode);

        // The group's last picture goes when the group does.
        var third = (await (await PutGroupAvatarAsync(alice, group.Id, Png, "image/png")).Content.ReadFromJsonAsync<ChatDto>())!.AvatarId;
        await bob.Http.DeleteAsync($"/api/chats/{group.Id}/members/{bob.User.Id}");
        await alice.Http.DeleteAsync($"/api/chats/{group.Id}/members/{alice.User.Id}");
        using (var scope = server.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Files.AnyAsync(x => x.Id == third));
    }

    [Fact]
    public async Task A_reply_quotes_its_message_and_says_when_that_is_gone()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);
        var question = await alice.SendAsync(chat.Id, "Are you coming tonight? " + new string('x', 200));

        var bobGot = bob.Next<MessageDto>("MessageReceived");
        var answer = await bob.ReplyAsync(chat.Id, "Yes", question.Id);
        Assert.Equal(question.Id, answer.ReplyTo!.Id);
        Assert.Equal("alice", answer.ReplyTo.SenderName);
        // Only the start of a long message is quoted.
        Assert.StartsWith("Are you coming tonight?", answer.ReplyTo.Summary);
        Assert.True(answer.ReplyTo.Summary.Length <= 100);
        Assert.Equal(question.Id, (await bobGot).ReplyTo!.Id);

        // A reply to a message of another chat is just a message.
        var other = await alice.GroupWithAsync("team", bob);
        Assert.Null((await bob.ReplyAsync(other.Id, "Hm", question.Id)).ReplyTo);

        await alice.Hub.InvokeAsync("DeleteMessage", question.Id);
        var history = (await bob.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages"))!;
        Assert.True(Assert.Single(history).ReplyTo!.Deleted);
    }

    [Fact]
    public async Task Own_messages_can_be_edited_and_deleted_for_everyone_and_others_cannot()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);
        var sent = await alice.SendAsync(chat.Id, "Helo");

        var bobSaw = bob.Next<MessageDto>("MessageUpdated");
        var edited = await alice.Hub.InvokeAsync<MessageDto>("EditMessage", sent.Id, " Hello ");
        Assert.Equal("Hello", edited.Text);
        Assert.NotNull(edited.EditedAt);
        Assert.Equal(("Hello", sent.Id), ((await bobSaw).Text, (await bobSaw).Id));

        await Assert.ThrowsAsync<HubException>(() => bob.Hub.InvokeAsync<MessageDto>("EditMessage", sent.Id, "mine now"));
        await Assert.ThrowsAsync<HubException>(() => alice.Hub.InvokeAsync<MessageDto>("EditMessage", sent.Id, "   "));
        await Assert.ThrowsAsync<HubException>(() => bob.Hub.InvokeAsync("DeleteMessage", sent.Id));

        // A message with a photo loses it along with the message.
        var photo = await UploadAsync(alice, chat.Id, Png, "a.png", "image/png");
        var album = await alice.Hub.InvokeAsync<MessageDto>("SendAttachments", chat.Id, "look", Guid.NewGuid(), new[] { photo.Id });
        // Its caption may go: the photo is still a message.
        Assert.Equal("", (await alice.Hub.InvokeAsync<MessageDto>("EditMessage", album.Id, "")).Text);
        var link = (await bob.Http.GetFromJsonAsync<FileLinkDto>($"/api/files/{photo.Id}/link"))!;

        var deleted = bob.Next<Guid, long>("MessageDeleted");
        await alice.Hub.InvokeAsync("DeleteMessage", album.Id);
        Assert.Equal((chat.Id, album.Id), await deleted);
        var history = (await bob.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages"))!;
        Assert.Equal([sent.Id], history.Select(message => message.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Http.GetAsync($"/api/files/{photo.Id}/link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Storage.GetAsync(link.Url)).StatusCode);
    }

    [Fact]
    public async Task Reactions_are_one_per_person_and_reach_everyone()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var stranger = await server.RegisterAsync("stranger");
        var chat = await alice.DirectChatWithAsync(bob);
        var sent = await alice.SendAsync(chat.Id, "Done!");

        var aliceSaw = alice.Next<MessageDto>("MessageUpdated");
        await bob.Hub.InvokeAsync<MessageDto>("React", sent.Id, "👍");
        Assert.Equal([bob.User.Id], Assert.Single((await aliceSaw).Reactions!).UserIds);

        await alice.Hub.InvokeAsync<MessageDto>("React", sent.Id, "👍");
        // A second reaction replaces the first.
        var changed = await bob.Hub.InvokeAsync<MessageDto>("React", sent.Id, "🔥");
        Assert.Equal([("👍", 1), ("🔥", 1)], changed.Reactions!.Select(r => (r.Emoji, r.UserIds.Count)).OrderBy(r => r.Item1));
        var history = (await alice.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages"))!;
        Assert.Equal(2, Assert.Single(history).Reactions!.Count);

        Assert.Null((await bob.Hub.InvokeAsync<MessageDto>("React", sent.Id, null)).Reactions!.SingleOrDefault(r => r.Emoji == "🔥"));
        await Assert.ThrowsAsync<HubException>(() => bob.Hub.InvokeAsync<MessageDto>("React", sent.Id, "💩"));
        await Assert.ThrowsAsync<HubException>(() => stranger.Hub.InvokeAsync<MessageDto>("React", sent.Id, "👍"));
    }

    [Fact]
    public async Task A_forwarded_message_names_its_author_and_has_its_own_copy_of_the_files()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var carol = await server.RegisterAsync("carol");
        var withBob = await alice.DirectChatWithAsync(bob);
        var withCarol = await bob.DirectChatWithAsync(carol);
        var photo = await UploadAsync(alice, withBob.Id, Png, "a.png", "image/png", 10, 10);
        var original = await alice.Hub.InvokeAsync<MessageDto>("SendAttachments", withBob.Id, "our team", Guid.NewGuid(), new[] { photo.Id });

        var carolGot = carol.Next<MessageDto>("MessageReceived");
        var forwarded = await bob.Hub.InvokeAsync<MessageDto>("Forward", original.Id, withCarol.Id);
        Assert.Equal(("alice", bob.User.Id, "our team"), (forwarded.ForwardedFrom, forwarded.Sender.Id, forwarded.Text));
        var copy = Assert.Single((await carolGot).Attachments!);
        Assert.NotEqual(photo.Id, copy.Id);
        Assert.Equal((10, 10), (copy.Width!.Value, copy.Height!.Value));

        // Carol has her own copy, which outlives the original.
        await alice.Hub.InvokeAsync("DeleteMessage", original.Id);
        var link = (await carol.Http.GetFromJsonAsync<FileLinkDto>($"/api/files/{copy.Id}/link"))!;
        using (var download = await Storage.GetAsync(link.Url))
            Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());

        // Forwarded again, it still names Alice; and nobody forwards into a chat they aren't in.
        var again = await carol.Hub.InvokeAsync<MessageDto>("Forward", forwarded.Id, withCarol.Id);
        Assert.Equal("alice", again.ForwardedFrom);
        await Assert.ThrowsAsync<HubException>(() => carol.Hub.InvokeAsync<MessageDto>("Forward", forwarded.Id, withBob.Id));
    }

    [Fact]
    public async Task Typing_reaches_the_others_and_search_finds_text()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);

        var typing = bob.Next<Guid, Guid>("UserTyping");
        await alice.Hub.InvokeAsync("Typing", chat.Id);
        Assert.Equal((chat.Id, alice.User.Id), await typing);

        await alice.SendAsync(chat.Id, "Meeting at 10");
        await bob.SendAsync(chat.Id, "the MEETING moved");
        await bob.SendAsync(chat.Id, "100% sure");
        await alice.SendAsync(chat.Id, "nothing here");
        var found = (await bob.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/search?q=meeting"))!;
        Assert.Equal(["the MEETING moved", "Meeting at 10"], found.Select(message => message.Text));
        // % is searched for as itself, not as "anything".
        Assert.Equal("100% sure", Assert.Single((await bob.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/search?q=0%25"))!).Text);
        var stranger = await server.RegisterAsync("stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/chats/{chat.Id}/search?q=meeting")).StatusCode);
    }

    [Fact]
    public async Task A_deleted_group_takes_its_files_along()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var group = await alice.GroupWithAsync("team", bob);
        var file = await UploadAsync(alice, group.Id, [1, 2, 3], "a.png", "image/png");
        var storage = server.Services.GetRequiredService<IFileStorage>();

        await bob.Http.DeleteAsync($"/api/chats/{group.Id}/members/{bob.User.Id}");
        await alice.Http.DeleteAsync($"/api/chats/{group.Id}/members/{alice.User.Id}");

        using var scope = server.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Files.AnyAsync(x => x.Id == file.Id));
        var link = storage.LinkFor(file.Id.ToString("N"), TimeSpan.FromMinutes(1), null);
        Assert.Equal(HttpStatusCode.NotFound, (await Storage.GetAsync(link)).StatusCode);
    }

    [Fact]
    public async Task Unanswered_call_leaves_a_missed_call_in_the_chat()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var bobDevice = await bob.RegisterDeviceAsync();
        var chat = await alice.DirectChatWithAsync(bob);

        var started = await alice.Hub.InvokeAsync<CallJoinResult>("StartCall", chat.Id, true);
        var bobGot = bob.Next<MessageDto>("MessageReceived");
        await alice.Hub.InvokeAsync("LeaveCall", started.CallId);

        var record = await bobGot;
        Assert.Equal(MessageKind.Call, record.Kind);
        Assert.Equal(new CallSummary(true, null), record.Call);
        Assert.Equal(alice.User.Id, record.Sender.Id);
        Assert.Equal("Видеозвонок без ответа", record.Text);

        var history = await bob.Http.GetFromJsonAsync<List<MessageDto>>($"/api/chats/{chat.Id}/messages");
        var stored = Assert.Single(history!);
        Assert.Equal((record.Id, record.Kind, record.Call), (stored.Id, stored.Kind, stored.Call));

        // Missed by bob only: the caller was there.
        var chats = await bob.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats");
        Assert.Equal(1, chats!.Single(x => x.Id == chat.Id).UnreadCount);
        chats = await alice.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats");
        Assert.Equal(0, chats!.Single(x => x.Id == chat.Id).UnreadCount);

        var push = await server.Push.NextAsync(bobDevice, PushKeys.TypeMessage);
        Assert.Equal(("alice", "Пропущенный видеозвонок"), (push[PushKeys.Sender], push[PushKeys.Text]));
    }

    [Fact]
    public async Task Answered_call_is_recorded_with_its_length_and_read_by_both()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var bobDevice = await bob.RegisterDeviceAsync();
        var chat = await alice.DirectChatWithAsync(bob);

        var started = await alice.Hub.InvokeAsync<CallJoinResult>("StartCall", chat.Id, false);
        await bob.Hub.InvokeAsync<CallJoinResult>("JoinCall", started.CallId);
        await Task.Delay(1100);
        await bob.Hub.InvokeAsync("LeaveCall", started.CallId);
        var aliceGot = alice.Next<MessageDto>("MessageReceived");
        await alice.Hub.InvokeAsync("LeaveCall", started.CallId);

        var record = await aliceGot;
        Assert.False(record.Call!.Video);
        Assert.InRange(record.Call.DurationSeconds!.Value, 1, 10);
        Assert.StartsWith("Звонок, 0:0", record.Text);

        foreach (var client in new[] { alice, bob })
        {
            var chats = await client.Http.GetFromJsonAsync<List<ChatDto>>("/api/chats");
            Assert.Equal(0, chats!.Single(x => x.Id == chat.Id).UnreadCount);
        }
        // Bob answered, so no missed call for him.
        await Task.Delay(300);
        Assert.Equal(0, server.Push.CountOf(bobDevice, PushKeys.TypeMessage));
    }

    [Fact]
    public async Task Presence_follows_the_app_and_reaches_only_contacts()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var mallory = await server.RegisterAsync("mallory");
        await alice.DirectChatWithAsync(bob);

        async Task<Presence?> PresenceOf(Client viewer, Client user) =>
            (await viewer.Http.GetFromJsonAsync<List<Presence>>("/api/presence"))!.SingleOrDefault(x => x.UserId == user.User.Id);

        Assert.True((await PresenceOf(bob, alice))!.Online);
        Assert.Null(await PresenceOf(mallory, alice));

        // Leaving the app on the only connection: offline, with the time.
        var away = bob.Next<Presence>("PresenceChanged");
        await alice.Hub.InvokeAsync("SetActive", false);
        var left = await away;
        Assert.Equal(alice.User.Id, left.UserId);
        Assert.False(left.Online);
        Assert.NotNull(left.LastSeenAt);
        Assert.False((await PresenceOf(bob, alice))!.Online);
        Assert.Equal(left.LastSeenAt!.Value.ToUnixTimeSeconds(), (await PresenceOf(bob, alice))!.LastSeenAt!.Value.ToUnixTimeSeconds());

        var back = bob.Next<Presence>("PresenceChanged");
        await alice.Hub.InvokeAsync("SetActive", true);
        Assert.True((await back).Online);

        // Closing the connection is leaving too.
        var closed = bob.Next<Presence>("PresenceChanged");
        await alice.Hub.StopAsync();
        Assert.False((await closed).Online);
        Assert.False((await PresenceOf(bob, alice))!.Online);
    }

    [Fact]
    public async Task Outsiders_cannot_mark_a_chat_read()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var mallory = await server.RegisterAsync("mallory");
        var chat = await alice.DirectChatWithAsync(bob);
        var sent = await alice.SendAsync(chat.Id, "hi");

        await Assert.ThrowsAsync<HubException>(() => mallory.Hub.InvokeAsync("MarkRead", chat.Id, sent.Id));
    }

    [Fact]
    public async Task Call_rings_connects_relays_signals_and_ends()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);

        var invited = bob.Next<CallInvite>("CallInvited");
        var started = await alice.Hub.InvokeAsync<CallJoinResult>("StartCall", chat.Id, true);
        Assert.Empty(started.Others);
        var invite = await invited;
        Assert.True(invite.Video);
        Assert.Equal(alice.User.Id, invite.Caller.Id);

        var aliceSawJoin = alice.Next<CallParticipant>("CallParticipantJoined");
        var joined = await bob.Hub.InvokeAsync<CallJoinResult>("JoinCall", invite.CallId);
        Assert.Equal(started.ParticipantId, Assert.Single(joined.Others).ParticipantId);
        Assert.Equal(bob.User.Id, (await aliceSawJoin).User.Id);

        // The newcomer offers, and the signal says who it came from so the answer can go back.
        var aliceGotOffer = alice.Next<CallSignal>("CallSignalReceived");
        await bob.Hub.InvokeAsync("SendCallSignal", joined.CallId, started.ParticipantId, "offer", "sdp");
        var offer = await aliceGotOffer;
        Assert.Equal(("offer", "sdp", joined.ParticipantId), (offer.Type, offer.Payload, offer.FromParticipantId));

        var aliceSawLeave = alice.Next<CallParticipant>("CallParticipantLeft");
        await bob.Hub.InvokeAsync("LeaveCall", joined.CallId);
        Assert.Equal(bob.User.Id, (await aliceSawLeave).User.Id);

        var bobSawEnd = bob.Next<Guid>("CallEnded");
        await alice.Hub.InvokeAsync("LeaveCall", joined.CallId);
        Assert.Equal(joined.CallId, await bobSawEnd);
    }

    [Fact]
    public async Task Decline_notifies_the_caller()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);
        var started = await alice.Hub.InvokeAsync<CallJoinResult>("StartCall", chat.Id, false);

        var seen = new TaskCompletionSource<UserDto>();
        using var _ = alice.Hub.On<Guid, UserDto>("CallDeclined", (_, user) => seen.TrySetResult(user));
        await bob.Hub.InvokeAsync("DeclineCall", started.CallId);

        Assert.Equal(bob.User.Id, (await seen.Task.WaitAsync(TimeSpan.FromSeconds(10))).Id);
    }

    [Fact]
    public async Task Disconnecting_leaves_the_call()
    {
        var alice = await server.RegisterAsync("alice");
        var bob = await server.RegisterAsync("bob");
        var chat = await alice.DirectChatWithAsync(bob);
        var started = await alice.Hub.InvokeAsync<CallJoinResult>("StartCall", chat.Id, true);
        await bob.Hub.InvokeAsync<CallJoinResult>("JoinCall", started.CallId);

        var left = alice.Next<CallParticipant>("CallParticipantLeft");
        await bob.Hub.StopAsync();
        Assert.Equal(bob.User.Id, (await left).User.Id);
    }

    [Fact]
    public async Task Group_call_holds_at_most_the_limit_and_outsiders_cannot_signal()
    {
        var clients = new List<Client>();
        for (var i = 0; i <= CallLimits.MaxParticipants; i++)
            clients.Add(await server.RegisterAsync("user" + i));
        var mallory = await server.RegisterAsync("mallory");
        var group = await clients[0].GroupWithAsync("big", [.. clients.Skip(1)]);

        var started = await clients[0].Hub.InvokeAsync<CallJoinResult>("StartCall", group.Id, true);
        for (var i = 1; i < CallLimits.MaxParticipants; i++)
        {
            var joined = await clients[i].Hub.InvokeAsync<CallJoinResult>("JoinCall", started.CallId);
            Assert.Equal(i, joined.Others.Count);
        }

        await Assert.ThrowsAsync<HubException>(() => clients[^1].Hub.InvokeAsync<CallJoinResult>("JoinCall", started.CallId));
        await Assert.ThrowsAsync<HubException>(() => mallory.Hub.InvokeAsync<CallJoinResult>("JoinCall", started.CallId));
        await Assert.ThrowsAsync<HubException>(() => mallory.Hub.InvokeAsync("SendCallSignal", started.CallId, started.ParticipantId, "offer", "x"));
    }
}
