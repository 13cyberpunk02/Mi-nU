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
        // A picture comes with its width and height, which the app measured; the server doesn't decode images.
        api.MapPost("/chats/{chatId:guid}/files", async (Guid chatId, IFormFile? file, [FromForm] int? width, [FromForm] int? height,
            ChatService chats, AppDbContext db, IFileStorage storage, ILogger<StoredFile> logger, ClaimsPrincipal principal,
            CancellationToken cancel) =>
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

            await using var content = file.OpenReadStream();
            var contentType = InlineTypes.Contains(file.ContentType) ? file.ContentType.ToLowerInvariant() : Download;
            // Only what really is a picture is shown as one; the rest of the claim is ignored.
            if (contentType.StartsWith("image/") && !await LooksLikeImageAsync(content, cancel))
                contentType = Download;
            var isImage = contentType.StartsWith("image/");
            var stored = new StoredFile
            {
                OwnerId = me,
                ChatId = chatId,
                Name = CleanName(file.FileName),
                ContentType = contentType,
                Size = file.Length,
                Width = isImage && width is > 0 and <= 20_000 ? width : null,
                Height = isImage && height is > 0 and <= 20_000 ? height : null,
            };
            try
            {
                await storage.PutAsync(stored.Key, content, stored.ContentType, cancel);
            }
            catch (Exception e) when (e is Amazon.Runtime.AmazonServiceException or HttpRequestException)
            {
                // Storage is down or refuses, out of space say. Its own log has the reason; the app gets a plain answer.
                logger.LogError(e, "Storage refused file {Key} ({Size} bytes)", stored.Key, stored.Size);
                return Results.Json(new { error = "File storage is unavailable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

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

    public static FileDto ToDto(StoredFile file) => ChatService.ToDto(file);

    static readonly byte[] JpegStart = [0xFF, 0xD8, 0xFF];
    static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Whether the bytes start like a picture an app can show: JPEG, PNG, GIF, WebP or HEIC. Leaves the stream at its start.
    /// </summary>
    static async Task<bool> LooksLikeImageAsync(Stream content, CancellationToken cancel)
    {
        var head = new byte[12];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancel);
        content.Position = 0;
        if (read < head.Length)
            return false;

        ReadOnlySpan<byte> bytes = head;
        return bytes[..3].SequenceEqual(JpegStart)
            || bytes[..8].SequenceEqual(PngStart)
            || bytes[..4].SequenceEqual("GIF8"u8)
            || (bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            || (bytes[4..8].SequenceEqual("ftyp"u8) && (bytes[8..12].SequenceEqual("heic"u8) || bytes[8..12].SequenceEqual("heix"u8)
                || bytes[8..12].SequenceEqual("mif1"u8) || bytes[8..12].SequenceEqual("heif"u8)));
    }

    // Keeps only the name itself, whatever path or control characters a client sent along.
    static string CleanName(string? name)
    {
        var clean = new string(Path.GetFileName(name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0)
            clean = "file";
        return clean.Length <= Limits.FileNameMax ? clean : clean[..Limits.FileNameMax];
    }
}
