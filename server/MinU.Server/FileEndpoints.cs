using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MinU.Contracts;

namespace MinU.Server;

public static partial class Endpoints
{
    /// <summary>
    /// Types a link may show in place: pictures, sound and video an app or a browser plays itself.
    /// Anything else, SVG and HTML included, is served as a download, so a file can never run as a page.
    /// </summary>
    static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "image/heic", "image/heif",
        "audio/mpeg", "audio/mp4", "audio/aac", "audio/ogg", "audio/opus", "audio/webm", "audio/wav",
        "video/mp4", "video/webm", "video/quicktime",
    };

    const string Download = "application/octet-stream";

    public static void MapFiles(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        // Uploads into a chat the user is in. The file isn't shown to anyone until it is sent in a message.
        api.MapPost("/chats/{chatId:guid}/files", async (Guid chatId, IFormFile? file, ChatService chats, AppDbContext db,
            IFileStorage storage, ClaimsPrincipal principal, CancellationToken cancel) =>
        {
            if (!storage.IsEnabled)
                return Results.Json(new { error = "Files are not set up on this server." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var me = UserId(principal);
            if (!await chats.IsMemberAsync(chatId, me))
                return Results.NotFound();
            if (file is null || file.Length == 0)
                return Problem("Attach a file.");
            if (file.Length > Limits.FileMax)
                return Problem($"A file can be at most {Limits.FileMax / 1024 / 1024} MB.");

            var stored = new StoredFile
            {
                OwnerId = me,
                ChatId = chatId,
                Name = CleanName(file.FileName),
                ContentType = InlineTypes.Contains(file.ContentType) ? file.ContentType.ToLowerInvariant() : Download,
                Size = file.Length,
            };
            await using (var content = file.OpenReadStream())
                await storage.PutAsync(stored.Key, content, stored.ContentType, cancel);

            db.Files.Add(stored);
            try
            {
                await db.SaveChangesAsync(cancel);
            }
            catch
            {
                // No row means nobody could ever reach the bytes; they go too.
                await storage.DeleteAsync(stored.Key, CancellationToken.None);
                throw;
            }
            return Results.Ok(ToDto(stored));
        })
        // Above Kestrel's default of 30 MB; the multipart framing needs a little room on top of the file.
        .WithMetadata(new RequestSizeLimitAttribute(Limits.FileMax + 1024 * 1024))
        .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = Limits.FileMax + 1024 * 1024 })
        // The API authenticates with a bearer token, not cookies, so there is no cross-site form to guard against.
        .DisableAntiforgery();

        // A link for downloading, for whoever may see the file: the members of its chat.
        api.MapGet("/files/{fileId:guid}/link", async (Guid fileId, ChatService chats, AppDbContext db, IFileStorage storage,
            StorageOptions options, ClaimsPrincipal principal) =>
        {
            if (!storage.IsEnabled)
                return Results.Json(new { error = "Files are not set up on this server." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId);
            // The same answer for a missing file and someone else's, so ids can't be probed.
            if (file?.ChatId is not { } chatId || !await chats.IsMemberAsync(chatId, UserId(principal)))
                return Results.NotFound();

            var lifetime = TimeSpan.FromMinutes(options.LinkMinutes);
            var url = storage.LinkFor(file.Key, lifetime, file.ContentType == Download ? file.Name : null);
            return Results.Ok(new FileLinkDto(url, DateTimeOffset.UtcNow + lifetime));
        });
    }

    public static FileDto ToDto(StoredFile file) => new(file.Id, file.Name, file.ContentType, file.Size);

    // Keeps only the name itself, whatever path or control characters a client sent along.
    static string CleanName(string? name)
    {
        var clean = new string(Path.GetFileName(name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0)
            clean = "file";
        return clean.Length <= Limits.FileNameMax ? clean : clean[..Limits.FileNameMax];
    }
}
