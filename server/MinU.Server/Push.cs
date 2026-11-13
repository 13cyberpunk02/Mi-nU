using System.Threading.Channels;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using FcmMessage = FirebaseAdmin.Messaging.Message;
using MinU.Contracts;

namespace MinU.Server;

public enum PushResult { Sent, InvalidToken, Failed }

public interface IPushSender
{
    Task<PushResult> SendAsync(string token, IReadOnlyDictionary<string, string> data, TimeSpan timeToLive, CancellationToken cancel);
}

/// <summary>Firebase Cloud Messaging, for Android. Data-only and high priority, so the app wakes and decides what to show.</summary>
public class FcmPushSender(FirebaseMessaging messaging, ILogger<FcmPushSender> logger) : IPushSender
{
    public static FcmPushSender Create(string credentialsFile, ILogger<FcmPushSender> logger)
    {
        var app = FirebaseApp.Create(new AppOptions { Credential = CredentialFactory.FromFile<ServiceAccountCredential>(credentialsFile).ToGoogleCredential() });
        return new FcmPushSender(FirebaseMessaging.GetMessaging(app), logger);
    }

    public async Task<PushResult> SendAsync(string token, IReadOnlyDictionary<string, string> data, TimeSpan timeToLive, CancellationToken cancel)
    {
        try
        {
            await messaging.SendAsync(new FcmMessage
            {
#pragma warning disable CS0618 // FirebaseAdmin 3.7 deprecates Token for Fid; sending by registration token still works.
                Token = token,
#pragma warning restore CS0618
                Data = data,
                Android = new AndroidConfig { Priority = Priority.High, TimeToLive = timeToLive },
            }, cancel);
            return PushResult.Sent;
        }
        catch (FirebaseMessagingException e) when (e.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument or MessagingErrorCode.SenderIdMismatch)
        {
            return PushResult.InvalidToken;
        }
        catch (FirebaseMessagingException e)
        {
            logger.LogWarning(e, "Push failed: {Code}", e.MessagingErrorCode);
            return PushResult.Failed;
        }
    }
}

/// <summary>Used when no Firebase credentials are configured: the app then works without push.</summary>
public class NoPushSender : IPushSender
{
    public Task<PushResult> SendAsync(string token, IReadOnlyDictionary<string, string> data, TimeSpan timeToLive, CancellationToken cancel) =>
        Task.FromResult(PushResult.Sent);
}

public record PushJob(IReadOnlyList<Guid> Recipients, IReadOnlyDictionary<string, string> Data, TimeSpan TimeToLive);

/// <summary>
/// Push goes out in the background: a message or a ringing call never waits for Google.
/// Whoever is in the app gets the same events through SignalR, and the app ignores the push then.
/// </summary>
public class PushQueue
{
    readonly Channel<PushJob> _jobs = Channel.CreateBounded<PushJob>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<PushJob> Reader => _jobs.Reader;

    public void Message(IEnumerable<Guid> recipients, MessageDto message, string? groupTitle) =>
        Enqueue(recipients, TimeSpan.FromDays(1), new()
        {
            [PushKeys.Type] = PushKeys.TypeMessage,
            [PushKeys.ChatId] = message.ChatId.ToString(),
            [PushKeys.ChatTitle] = groupTitle ?? "",
            [PushKeys.Sender] = message.Sender.DisplayName,
            [PushKeys.Text] = message.Text.Length <= Limits.PushTextMax ? message.Text : message.Text[..Limits.PushTextMax] + "…",
        });

    // A call nobody answered within the minute is over: the push must not ring later.
    public void Call(IEnumerable<Guid> recipients, CallInvite invite) =>
        Enqueue(recipients, TimeSpan.FromMinutes(1), new()
        {
            [PushKeys.Type] = PushKeys.TypeCall,
            [PushKeys.CallId] = invite.CallId.ToString(),
            [PushKeys.ChatId] = invite.ChatId.ToString(),
            [PushKeys.Sender] = invite.Caller.DisplayName,
            [PushKeys.Video] = invite.Video ? "1" : "0",
        });

    public void CallEnded(IEnumerable<Guid> recipients, Guid callId) =>
        Enqueue(recipients, TimeSpan.FromMinutes(1), new()
        {
            [PushKeys.Type] = PushKeys.TypeCallEnded,
            [PushKeys.CallId] = callId.ToString(),
        });

    void Enqueue(IEnumerable<Guid> recipients, TimeSpan timeToLive, Dictionary<string, string> data)
    {
        var list = recipients.ToList();
        if (list.Count > 0)
            _jobs.Writer.TryWrite(new PushJob(list, data, timeToLive));
    }
}

public class PushWorker(PushQueue queue, IPushSender sender, IServiceScopeFactory scopes, ILogger<PushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stop))
        {
            try
            {
                await SendAsync(job, stop);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Push job failed");
            }
        }
    }

    async Task SendAsync(PushJob job, CancellationToken stop)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var devices = await db.DeviceTokens.Where(device => job.Recipients.Contains(device.UserId)).ToListAsync(stop);

        var invalid = new List<DeviceToken>();
        foreach (var device in devices)
        {
            if (await sender.SendAsync(device.Token, job.Data, job.TimeToLive, stop) == PushResult.InvalidToken)
                invalid.Add(device);
        }

        // The app was uninstalled or its token replaced: stop sending there.
        if (invalid.Count > 0)
        {
            db.DeviceTokens.RemoveRange(invalid);
            await db.SaveChangesAsync(stop);
        }
    }
}
