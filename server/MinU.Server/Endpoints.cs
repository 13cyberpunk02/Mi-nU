using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MinU.Contracts;

namespace MinU.Server;

public static partial class Endpoints
{
    public const string AuthRateLimit = "auth";

    // Compared against when the user doesn't exist, so that login takes as long as for a real account.
    static readonly string UnknownUserHash = new PasswordHasher<User>()
        .HashPassword(new User { UserName = "", NormalizedUserName = "", DisplayName = "" }, Guid.NewGuid().ToString());

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting(AuthRateLimit);

        auth.MapPost("/register", async (RegisterRequest request, AppDbContext db, IPasswordHasher<User> hasher, IConfiguration config) =>
        {
            var userName = request.UserName?.Trim() ?? "";
            var displayName = request.DisplayName?.Trim() ?? "";
            if (!UserNamePattern().IsMatch(userName))
                return Problem($"A username must be {Limits.UserNameMin} to {Limits.UserNameMax} Latin letters, digits or underscores.");
            if (displayName.Length is 0 or > Limits.DisplayNameMax)
                return Problem($"A display name must be 1 to {Limits.DisplayNameMax} characters long.");
            if (request.Password is null || request.Password.Length < Limits.PasswordMin)
                return Problem($"A password must be at least {Limits.PasswordMin} characters long.");

            var user = new User { UserName = userName, NormalizedUserName = userName.ToUpperInvariant(), DisplayName = displayName };
            user.PasswordHash = hasher.HashPassword(user, request.Password);
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException e) when (ChatService.IsUniqueViolation(e))
            {
                return Results.Conflict(new { error = "This username is taken." });
            }
            return Results.Ok(await IssueTokensAsync(user, db, config));
        });

        auth.MapPost("/login", async (LoginRequest request, AppDbContext db, IPasswordHasher<User> hasher, IConfiguration config) =>
        {
            var normalized = request.UserName?.Trim().ToUpperInvariant() ?? "";
            var user = await db.Users.FirstOrDefaultAsync(x => x.NormalizedUserName == normalized);
            var verified = hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? UnknownUserHash, request.Password ?? "");
            if (user is null || verified == PasswordVerificationResult.Failed)
                return Results.Unauthorized();

            if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, request.Password!);
                await db.SaveChangesAsync();
            }
            return Results.Ok(await IssueTokensAsync(user, db, config));
        });

        // Outside the sign-in rate limit: a refresh token is 256 random bits, and many devices may refresh at once behind one address.
        app.MapPost("/api/auth/refresh", async (RefreshRequest request, AppDbContext db, IConfiguration config) =>
            await RefreshAsync(request.RefreshToken, db, config) is { } tokens ? Results.Ok(tokens) : Results.Unauthorized());

        // Signing out ends this device's sign-in, so its refresh token can't be used even if it was copied.
        app.MapPost("/api/auth/logout", async (RefreshRequest request, AppDbContext db) =>
        {
            var hash = Hash(request.RefreshToken ?? "");
            await db.RefreshTokens.Where(x => x.Hash == hash && x.RevokedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));
            return Results.NoContent();
        });
    }

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/ice-servers", (ClaimsPrincipal principal, IConfiguration config) =>
            Results.Ok(TurnCredentials.Issue(config, UserId(principal))));

        api.MapPut("/devices", async (RegisterDeviceRequest request, AppDbContext db, ClaimsPrincipal principal) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > Limits.DeviceTokenMax || request.Platform != "android")
                return Problem("Unknown device.");

            var me = UserId(principal);
            var device = await db.DeviceTokens.FirstOrDefaultAsync(x => x.Token == request.Token);
            if (device is null)
                db.DeviceTokens.Add(new DeviceToken { UserId = me, Token = request.Token, Platform = request.Platform });
            else
            {
                // Someone else signed in on this phone: the notifications are theirs now.
                device.UserId = me;
                device.UpdatedAt = DateTimeOffset.UtcNow;
            }
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException e) when (ChatService.IsUniqueViolation(e))
            {
                // The same phone registered twice at once; the other request already stored it.
            }
            return Results.NoContent();
        });

        // "Decline" on a ringing notification, which answers without opening the app or its SignalR connection.
        api.MapPost("/calls/{callId:guid}/decline", async (Guid callId, CallRegistry calls, ChatService chats, AppDbContext db,
            ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub) =>
        {
            var me = UserId(principal);
            if (calls.ChatOf(callId) is not { } chatId)
                return Results.NoContent();
            if (!await chats.IsMemberAsync(chatId, me))
                return Results.NotFound();

            var user = ChatService.ToDto(await db.Users.AsNoTracking().FirstAsync(x => x.Id == me));
            await hub.Clients.Clients(calls.ParticipantIds(callId)).CallDeclined(callId, user);
            return Results.NoContent();
        });

        // Signing out stops the notifications on that phone.
        api.MapDelete("/devices", async (string token, AppDbContext db, ClaimsPrincipal principal) =>
        {
            var me = UserId(principal);
            await db.DeviceTokens.Where(x => x.Token == token && x.UserId == me).ExecuteDeleteAsync();
            return Results.NoContent();
        });

        api.MapGet("/users", async (string? query, AppDbContext db, ClaimsPrincipal principal) =>
        {
            var text = query?.Trim() ?? "";
            if (text.Length < 2)
                return Results.Ok(Array.Empty<UserDto>());

            var me = UserId(principal);
            var pattern = "%" + EscapeLike(text) + "%";
            var users = await db.Users
                .Where(user => user.Id != me
                    && (EF.Functions.ILike(user.UserName, pattern, "\\") || EF.Functions.ILike(user.DisplayName, pattern, "\\")))
                .OrderBy(user => user.UserName)
                .Take(20)
                .Select(user => new UserDto(user.Id, user.UserName, user.DisplayName))
                .ToListAsync();
            return Results.Ok(users);
        });

        api.MapGet("/chats", async (ChatService chats, ClaimsPrincipal principal) =>
            Results.Ok(await chats.ChatsOfAsync(UserId(principal))));

        // Presence of everyone the user shares a chat with; changes then arrive as PresenceChanged.
        api.MapGet("/presence", async (ChatService chats, PresenceTracker presence, ClaimsPrincipal principal) =>
            Results.Ok(await chats.PresenceOfContactsAsync(UserId(principal), presence)));

        api.MapPost("/chats/direct", async (CreateDirectChatRequest request, ChatService chats, ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub) =>
        {
            var me = UserId(principal);
            var (chat, created) = await chats.GetOrCreateDirectAsync(me, request.UserId);
            if (chat is null)
                return Problem("That user can't be messaged.");
            if (created)
                await hub.Clients.User(request.UserId.ToString()).ChatCreated(ChatService.ToDto(chat, request.UserId, null));
            return Results.Ok(ChatService.ToDto(chat, me, null));
        });

        api.MapPost("/chats/group", async (CreateGroupChatRequest request, ChatService chats, ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub) =>
        {
            var title = request.Title?.Trim() ?? "";
            if (title.Length is 0 or > Limits.ChatTitleMax)
                return Problem($"A group title must be 1 to {Limits.ChatTitleMax} characters long.");
            if (request.MemberIds is null || request.MemberIds.Count is 0 or >= Limits.GroupMembersMax)
                return Problem($"A group needs 1 to {Limits.GroupMembersMax - 1} other members.");

            var me = UserId(principal);
            var chat = await chats.CreateGroupAsync(me, title, request.MemberIds);
            if (chat is null)
                return Problem("Some of those users don't exist.");

            var others = chat.Members.Select(member => member.UserId).Where(id => id != me).Select(id => id.ToString());
            await hub.Clients.Users(others).ChatCreated(ChatService.ToDto(chat, me, null));
            return Results.Ok(ChatService.ToDto(chat, me, null));
        });

        api.MapGet("/chats/{chatId:guid}/messages", async (Guid chatId, long? before, int? limit, ChatService chats, ClaimsPrincipal principal) =>
        {
            if (!await chats.IsMemberAsync(chatId, UserId(principal)))
                return Results.NotFound();
            return Results.Ok(await chats.HistoryAsync(chatId, before, Math.Clamp(limit ?? 50, 1, Limits.HistoryPageMax)));
        });

        // Any member may rename the group or add people; only the owner removes others, and anyone may leave.
        api.MapPut("/chats/{chatId:guid}/title", async (Guid chatId, RenameGroupRequest request, ChatService chats,
            ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub) =>
        {
            var title = request.Title?.Trim() ?? "";
            if (title.Length is 0 or > Limits.ChatTitleMax)
                return Problem($"A group title must be 1 to {Limits.ChatTitleMax} characters long.");
            var me = UserId(principal);
            if (await chats.FindGroupAsync(chatId, me) is not { } chat)
                return Results.NotFound();
            if (chat.Title == title)
                return Results.Ok(ChatService.ToDto(chat, me, null));

            await chats.RenameAsync(chatId, title);
            return Results.Ok(await GroupChangedAsync(chatId, me, $"{NameIn(chat, me)} переименовал(а) группу в «{title}»", chats, hub));
        });

        // A group's picture, changed like its title by any member. It is a file of the group: only members fetch it.
        api.MapPut("/chats/{chatId:guid}/avatar", async (Guid chatId, IFormFile? file, [Microsoft.AspNetCore.Mvc.FromForm] int? width,
            [Microsoft.AspNetCore.Mvc.FromForm] int? height, ChatService chats, AppDbContext db, IFileStorage storage,
            ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub, ILogger<StoredFile> logger, CancellationToken cancel) =>
        {
            var me = UserId(principal);
            if (await chats.FindGroupAsync(chatId, me) is not { } chat)
                return Results.NotFound();
            var (picture, refused) = await StorePictureAsync(file, width, height, me, chatId, storage, logger, cancel);
            if (picture is null)
                return refused!;

            var group = await db.Chats.FirstAsync(x => x.Id == chatId, cancel);
            var previous = group.AvatarFileId;
            db.Files.Add(picture);
            group.AvatarFileId = picture.Id;
            await db.SaveChangesAsync(cancel);
            await RemovePictureAsync(previous, db, storage, logger);
            return Results.Ok(await GroupChangedAsync(chatId, me, $"{NameIn(chat, me)} изменил(а) фото группы", chats, hub));
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(Limits.AvatarMax + 1024 * 1024))
        .DisableAntiforgery();

        api.MapDelete("/chats/{chatId:guid}/avatar", async (Guid chatId, ChatService chats, AppDbContext db, IFileStorage storage,
            ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub, ILogger<StoredFile> logger) =>
        {
            var me = UserId(principal);
            if (await chats.FindGroupAsync(chatId, me) is not { } chat)
                return Results.NotFound();
            var group = await db.Chats.FirstAsync(x => x.Id == chatId);
            if (group.AvatarFileId is not { } previous)
                return Results.Ok(ChatService.ToDto(chat, me, null));
            group.AvatarFileId = null;
            await db.SaveChangesAsync();
            await RemovePictureAsync(previous, db, storage, logger);
            return Results.Ok(await GroupChangedAsync(chatId, me, $"{NameIn(chat, me)} удалил(а) фото группы", chats, hub));
        });

        api.MapPost("/chats/{chatId:guid}/members", async (Guid chatId, AddMembersRequest request, ChatService chats,
            ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub) =>
        {
            var me = UserId(principal);
            if (await chats.FindGroupAsync(chatId, me) is not { } chat)
                return Results.NotFound();
            if (request.UserIds is null || request.UserIds.Count == 0)
                return Problem("Choose someone to add.");
            if (chat.Members.Count + request.UserIds.Distinct().Count() > Limits.GroupMembersMax)
                return Problem($"A group can have at most {Limits.GroupMembersMax} members.");

            var added = await chats.AddMembersAsync(chatId, request.UserIds);
            if (added.Count == 0)
                return Results.Ok(ChatService.ToDto(chat, me, null));
            var names = string.Join(", ", added.Select(user => user.DisplayName));
            return Results.Ok(await GroupChangedAsync(chatId, me, $"{NameIn(chat, me)} добавил(а) {names}", chats, hub,
                added: [.. added.Select(user => user.Id)]));
        });

        // Removing oneself is leaving.
        api.MapDelete("/chats/{chatId:guid}/members/{userId:guid}", async (Guid chatId, Guid userId, ChatService chats,
            ClaimsPrincipal principal, IHubContext<ChatHub, IChatClient> hub) =>
        {
            var me = UserId(principal);
            if (await chats.FindGroupAsync(chatId, me) is not { } chat || chat.Members.All(member => member.UserId != userId))
                return Results.NotFound();
            if (userId != me && chat.OwnerId != me)
                return Results.Json(new { error = "Only the group's owner can remove members." }, statusCode: StatusCodes.Status403Forbidden);

            var text = userId == me
                ? $"{NameIn(chat, me)} вышел(а) из группы"
                : $"{NameIn(chat, me)} удалил(а) {NameIn(chat, userId)}";
            if (!await chats.RemoveMemberAsync(chatId, userId))
            {
                await hub.Clients.User(userId.ToString()).ChatRemoved(chatId);
                return Results.NoContent();
            }
            await GroupChangedAsync(chatId, me, text, chats, hub, removed: userId);
            return Results.NoContent();
        });
    }

    static string NameIn(Chat chat, Guid userId) =>
        chat.Members.FirstOrDefault(member => member.UserId == userId)?.User.DisplayName ?? "";

    /// <summary>
    /// Writes the change into the group and tells everyone: the new chat to whoever was added, the update to the rest,
    /// the line about it to all of them, and to whoever was removed that the chat is gone for them.
    /// </summary>
    static async Task<ChatDto> GroupChangedAsync(Guid chatId, Guid actorId, string text, ChatService chats,
        IHubContext<ChatHub, IChatClient> hub, IReadOnlyCollection<Guid>? added = null, Guid? removed = null)
    {
        var line = await chats.AddSystemMessageAsync(chatId, actorId, text);
        var chat = (await chats.FindAsync(chatId))!;
        var dto = ChatService.ToDto(chat, actorId, null) with { LastMessage = line };
        var members = chat.Members.Select(member => member.UserId).ToList();
        added ??= [];

        if (added.Count > 0)
            await hub.Clients.Users(added.Select(id => id.ToString())).ChatCreated(dto);
        await hub.Clients.Users(members.Where(id => !added.Contains(id)).Select(id => id.ToString())).ChatUpdated(dto);
        await hub.Clients.Users(members.Select(id => id.ToString())).MessageReceived(line);
        if (removed is { } gone)
            await hub.Clients.User(gone.ToString()).ChatRemoved(chatId);
        return dto;
    }

    /// <summary>A new token pair; the refresh token's hash is stored for the next <see cref="RefreshAsync"/>.</summary>
    static async Task<AuthResponse> IssueTokensAsync(User user, AppDbContext db, IConfiguration config)
    {
        var now = DateTimeOffset.UtcNow;
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var refreshExpires = now.AddDays(config.GetValue("Jwt:RefreshDays", 60));
        // Expired sign-ins are kept until then only for spotting reuse; afterwards they are just clutter.
        await db.RefreshTokens.Where(x => x.UserId == user.Id && x.ExpiresAt < now).ExecuteDeleteAsync();
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, Hash = Hash(refresh), ExpiresAt = refreshExpires });
        await db.SaveChangesAsync();

        var (token, expires) = AccessToken(user, config);
        return new AuthResponse(token, expires, ChatService.ToDto(user), refresh, refreshExpires);
    }

    /// <summary>
    /// Swaps a refresh token for a new pair; each works once. A token that was already swapped coming back means
    /// someone copied it, so every sign-in of that user ends and both the thief and the owner must sign in again.
    /// </summary>
    static async Task<AuthResponse?> RefreshAsync(string? refreshToken, AppDbContext db, IConfiguration config)
    {
        var hash = Hash(refreshToken ?? "");
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(x => x.Hash == hash);
        if (stored is null)
            return null;
        if (stored.Replaced)
        {
            await db.RefreshTokens.Where(x => x.UserId == stored.UserId && x.RevokedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));
            return null;
        }
        if (stored.RevokedAt is not null || stored.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;
        if (await db.Users.FirstOrDefaultAsync(x => x.Id == stored.UserId) is not { } user)
            return null;

        stored.RevokedAt = DateTimeOffset.UtcNow;
        stored.Replaced = true;
        await db.SaveChangesAsync();
        return await IssueTokensAsync(user, db, config);
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static (string Token, DateTimeOffset Expires) AccessToken(User user, IConfiguration config)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(config.GetValue("Jwt:AccessMinutes", 60));
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString())],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey(config), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public static SymmetricSecurityKey SigningKey(IConfiguration config)
    {
        var key = config["Jwt:Key"];
        if (key is null || key.Length < 32)
            throw new InvalidOperationException("Set Jwt:Key to a secret of at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    static IResult Problem(string error) => Results.BadRequest(new { error });

    static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    [GeneratedRegex($"^[A-Za-z0-9_]{{3,32}}$")]
    private static partial Regex UserNamePattern();
}
