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

public sealed record Client(HttpClient Http, HubConnection Hub, UserDto User, string Token)
{
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
