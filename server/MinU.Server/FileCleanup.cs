using Microsoft.EntityFrameworkCore;

namespace MinU.Server;

/// <summary>
/// Removes files that were uploaded but never sent: the app closed halfway through sending, say.
/// Nobody can reach them, as only a message shows a file, so after a day they only take disk space.
/// </summary>
public class FileCleanup(IServiceScopeFactory scopes, IFileStorage storage, ILogger<FileCleanup> logger) : BackgroundService
{
    /// <summary>How long an upload may wait for its message: longer than any real send, retries included.</summary>
    public static readonly TimeSpan UnsentLifetime = TimeSpan.FromDays(1);
    static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!storage.IsEnabled)
            return;

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RemoveUnsentAsync(DateTimeOffset.UtcNow - UnsentLifetime, stop);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Tried again within the hour; a failed round loses nothing.
                logger.LogWarning(e, "Removing unsent files failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>Removes files uploaded before <paramref name="olderThan"/> that no message holds. Returns how many.</summary>
    public async Task<int> RemoveUnsentAsync(DateTimeOffset olderThan, CancellationToken cancel)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unsent = await db.Files
            // Files in chats only: a profile picture is in no message and no chat, and must stay.
            .Where(file => file.ChatId != null && file.MessageId == null && file.CreatedAt < olderThan)
            .Take(500)
            .ToListAsync(cancel);

        var removed = 0;
        foreach (var file in unsent)
        {
            // The row first, and only while still unsent: a message taking the file at this very moment keeps it.
            // A failure with the bytes after that only leaves unused bytes, never a message without its file.
            if (await db.Files.Where(x => x.Id == file.Id && x.MessageId == null).ExecuteDeleteAsync(cancel) == 0)
                continue;
            removed++;
            try
            {
                await storage.DeleteAsync(file.Key, cancel);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Could not delete the bytes of unsent file {Key}", file.Key);
            }
        }
        if (removed > 0)
            logger.LogInformation("Removed {Count} unsent files", removed);
        return removed;
    }
}
