using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MinU.Contracts;

namespace MinU.Server;

public static partial class Endpoints
{
    public static void MapProfile(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me").RequireAuthorization();

        me.MapPut("", async (UpdateProfileRequest request, AppDbContext db, ChatService chats, ClaimsPrincipal principal,
            IHubContext<ChatHub, IChatClient> hub) =>
        {
            var name = request.DisplayName?.Trim() ?? "";
            if (name.Length is 0 or > Limits.DisplayNameMax)
                return Problem($"A name must be 1 to {Limits.DisplayNameMax} characters long.");
            var user = await db.Users.FirstAsync(x => x.Id == UserId(principal));
            user.DisplayName = name;
            await db.SaveChangesAsync();
            return Results.Ok(await ProfileChangedAsync(user, chats, hub));
        });

        // A picture only: it is shown to anyone who meets the user, in a chat or a search.
        me.MapPut("/avatar", async (IFormFile? file, [FromForm] int? width, [FromForm] int? height, AppDbContext db,
            ChatService chats, IFileStorage storage, ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub,
            ILogger<StoredFile> logger, CancellationToken cancel) =>
        {
            if (!storage.IsEnabled)
                return Results.Json(new { error = "Files are not set up on this server." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            if (file is null || file.Length == 0)
                return Problem("Attach a picture.");
            if (file.Length > Limits.AvatarMax)
                return Problem($"A picture can be at most {Limits.AvatarMax / 1024 / 1024} MB.");
            await using var content = file.OpenReadStream();
            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || !InlineTypes.Contains(file.ContentType)
                || !await LooksLikeImageAsync(content, cancel))
                return Problem("A profile picture must be a JPEG, PNG, GIF, WebP or HEIC image.");

            var user = await db.Users.FirstAsync(x => x.Id == UserId(principal), cancel);
            var picture = new StoredFile
            {
                OwnerId = user.Id,
                Name = "avatar" + Path.GetExtension(CleanName(file.FileName)).ToLowerInvariant(),
                ContentType = file.ContentType.ToLowerInvariant(),
                Size = file.Length,
                Width = width is > 0 and <= 20_000 ? width : null,
                Height = height is > 0 and <= 20_000 ? height : null,
            };
            try
            {
                await storage.PutAsync(picture.Key, content, picture.ContentType, cancel);
            }
            catch (Exception e) when (e is Amazon.Runtime.AmazonServiceException or HttpRequestException)
            {
                logger.LogError(e, "Storage refused the profile picture of {UserId}", user.Id);
                return Results.Json(new { error = "File storage is unavailable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var previous = user.AvatarFileId;
            db.Files.Add(picture);
            user.AvatarFileId = picture.Id;
            await db.SaveChangesAsync(cancel);
            await RemovePictureAsync(previous, db, storage, logger);
            return Results.Ok(await ProfileChangedAsync(user, chats, hub));
        })
        .WithMetadata(new RequestSizeLimitAttribute(Limits.AvatarMax + 1024 * 1024))
        .DisableAntiforgery();

        me.MapDelete("/avatar", async (AppDbContext db, ChatService chats, IFileStorage storage, ClaimsPrincipal principal,
            IHubContext<ChatHub, IChatClient> hub, ILogger<StoredFile> logger) =>
        {
            var user = await db.Users.FirstAsync(x => x.Id == UserId(principal));
            var previous = user.AvatarFileId;
            if (previous is null)
                return Results.Ok(ChatService.ToDto(user));
            user.AvatarFileId = null;
            await db.SaveChangesAsync();
            await RemovePictureAsync(previous, db, storage, logger);
            return Results.Ok(await ProfileChangedAsync(user, chats, hub));
        });
    }

    /// <summary>The new profile to everyone who shares a chat with the user, and to the user's other devices.</summary>
    static async Task<UserDto> ProfileChangedAsync(User user, ChatService chats, IHubContext<ChatHub, IChatClient> hub)
    {
        var dto = ChatService.ToDto(user);
        var people = (await chats.ContactIdsAsync(user.Id)).Append(user.Id).Select(id => id.ToString());
        await hub.Clients.Users(people).UserUpdated(dto);
        return dto;
    }

    // A replaced picture is reachable by nobody any more; its bytes are only wasted space, so a failure is just logged.
    static async Task RemovePictureAsync(Guid? fileId, AppDbContext db, IFileStorage storage, ILogger logger)
    {
        if (fileId is not { } id)
            return;
        await db.Files.Where(file => file.Id == id).ExecuteDeleteAsync();
        try
        {
            await storage.DeleteAsync(id.ToString("N"), CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogWarning(e, "Could not delete the old profile picture {Key}", id.ToString("N"));
        }
    }
}
