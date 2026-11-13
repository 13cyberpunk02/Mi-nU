using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
            return Results.Ok(IssueToken(user, config));
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
            return Results.Ok(IssueToken(user, config));
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
    }

    static AuthResponse IssueToken(User user, IConfiguration config)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(config.GetValue("Jwt:LifetimeDays", 30));
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString())],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey(config), SecurityAlgorithms.HmacSha256));
        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, ChatService.ToDto(user));
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
