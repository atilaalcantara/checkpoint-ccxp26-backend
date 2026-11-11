using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Mvc;

namespace Checkpoint.Api;

public static class AdminAuth
{
    public static void MapAdminAuth(this WebApplication app)
    {
        app.MapGet("/api/admin/auth/bootstrap-status", (IConfiguration config, Store store, CancellationToken ct) => BootstrapStatus(config, store, ct)).RequireCors("admin");
        app.MapPost("/api/admin/passkeys/register/options", RegisterOptions).RequireRateLimiting("admin-auth").RequireCors("admin");
        app.MapPost("/api/admin/passkeys/register/verify", RegisterVerify).RequireRateLimiting("admin-auth").RequireCors("admin");
        app.MapPost("/api/admin/auth/passkey/options", LoginOptions).RequireRateLimiting("admin-auth").RequireCors("admin");
        app.MapPost("/api/admin/auth/passkey/verify", LoginVerify).RequireRateLimiting("admin-auth").RequireCors("admin");
        app.MapGet("/api/admin/auth/me", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
            ? Results.Ok(new { id = user.FindFirstValue(ClaimTypes.NameIdentifier), name = user.Identity.Name, role = "admin" })
            : Results.Unauthorized()).RequireAuthorization().RequireCors("admin");
        app.MapPost("/api/admin/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization().RequireCors("admin");
        app.MapGet("/api/admin/auth/csrf", (HttpContext context) =>
        {
            var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append("checkpoint.csrf", token, new CookieOptions { HttpOnly = false, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/api/admin", MaxAge = TimeSpan.FromHours(2) });
            return Results.Ok(new { token });
        }).RequireAuthorization().RequireCors("admin");
    }

    private static async Task<IResult> BootstrapStatus(IConfiguration config, Store store, CancellationToken ct)
    {
        if (!BootstrapConfigured(config)) return Results.Ok(new { enabled = false });
        await using var db = await store.Open(ct);
        var count = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(DISTINCT user_id) FROM checkpoint.admin_passkeys", cancellationToken: ct));
        return Results.Ok(new { enabled = count < 2 });
    }

    private static async Task<IResult> RegisterOptions(HttpContext ctx, Store store, IConfiguration config, IDistributedCacheChallenge challenges, [FromServices] IFido2 fido, [FromBody] AdminPasskeyRegisterRequest request, CancellationToken ct)
    {
        if (!BootstrapValid(ctx, config) || !Guid.TryParse(request.AdminId, out var userId)) return Results.Unauthorized();
        await using var db = await store.Open(ct);
        var user = await db.QuerySingleOrDefaultAsync<AdminRow>(new CommandDefinition("SELECT id,name FROM checkpoint.admin_users WHERE id=@userId", new { userId }, cancellationToken: ct));
        if (user is null || await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM checkpoint.admin_passkeys WHERE user_id=@userId)", new { userId }, cancellationToken: ct))) return Results.Conflict(new { error = "Este administrador já possui uma Passkey." });
        var options = fido.RequestNewCredential(new RequestNewCredentialParams {
            User = new Fido2User { Id = userId.ToByteArray(), Name = user.Name, DisplayName = user.Name },
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Required },
            AttestationPreference = AttestationConveyancePreference.None
        });
        await challenges.Put(ctx, options, userId.ToString(), ct);
        return Results.Ok(options);
    }

    private static async Task<IResult> RegisterVerify(HttpContext ctx, Store store, IConfiguration config, IDistributedCacheChallenge challenges, [FromServices] IFido2 fido, [FromBody] AuthenticatorAttestationRawResponse response, CancellationToken ct)
    {
        if (!BootstrapValid(ctx, config)) return Results.Unauthorized();
        var state = await challenges.Take(ctx, ct);
        if (state is null || !Guid.TryParse(state.UserId, out var userId)) return Results.BadRequest(new { error = "Challenge expirado. Solicite novamente." });
        try
        {
            var original = JsonSerializer.Deserialize<CredentialCreateOptions>(state.Options, WebJson.Options) ?? throw new InvalidOperationException();
            var result = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams {
                AttestationResponse = response, OriginalOptions = original,
                IsCredentialIdUniqueToUserCallback = async (args, token) => {
                    await using var db = await store.Open(token);
                    return !await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM checkpoint.admin_passkeys WHERE credential_id=@id)", new { id = args.CredentialId }, cancellationToken: token));
                }
            }, ct);
            await using var connection = await store.Open(ct);
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO checkpoint.admin_passkeys(credential_id,user_id,public_key,sign_count,user_handle) VALUES(@Id,@userId,@PublicKey,@signCount,@userHandle)", new { result.Id, userId, result.PublicKey, signCount = (long)result.SignCount, userHandle = userId.ToByteArray() }, cancellationToken: ct));
            return Results.Ok(new { success = true });
        }
        catch (Fido2VerificationException) { return Results.BadRequest(new { error = "A validação WebAuthn falhou." }); }
    }

    private static async Task<IResult> LoginOptions(HttpContext ctx, IDistributedCacheChallenge challenges, [FromServices] IFido2 fido, CancellationToken ct)
    {
        var options = fido.GetAssertionOptions(new GetAssertionOptionsParams { UserVerification = UserVerificationRequirement.Required });
        await challenges.Put(ctx, options, "", ct);
        return Results.Ok(options);
    }

    private static async Task<IResult> LoginVerify(HttpContext ctx, Store store, IDistributedCacheChallenge challenges, [FromServices] IFido2 fido, [FromBody] AuthenticatorAssertionRawResponse response, CancellationToken ct)
    {
        var state = await challenges.Take(ctx, ct);
        if (state is null) return Results.BadRequest(new { error = "Challenge expirado. Solicite novamente." });
        try
        {
            var options = JsonSerializer.Deserialize<AssertionOptions>(state.Options, WebJson.Options) ?? throw new InvalidOperationException();
            var id = response.RawId;
            await using var db = await store.Open(ct);
            var credential = await db.QuerySingleOrDefaultAsync<CredentialRow>(new CommandDefinition("SELECT c.credential_id,c.public_key,c.sign_count,c.user_handle,u.id,u.name FROM checkpoint.admin_passkeys c JOIN checkpoint.admin_users u ON u.id=c.user_id WHERE c.credential_id=@id", new { id }, cancellationToken: ct));
            if (credential is null) return Results.Unauthorized();
            var result = await fido.MakeAssertionAsync(new MakeAssertionParams {
                AssertionResponse = response, OriginalOptions = options, StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.SequenceEqual(credential.UserHandle))
            }, ct);
            await db.ExecuteAsync(new CommandDefinition("UPDATE checkpoint.admin_passkeys SET sign_count=@counter WHERE credential_id=@id", new { counter = (long)result.SignCount, id }, cancellationToken: ct));
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, credential.Id.ToString()), new Claim(ClaimTypes.Name, credential.Name), new Claim(ClaimTypes.Role, "admin") };
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok(new { success = true, name = credential.Name });
        }
        catch (Fido2VerificationException) { return Results.Unauthorized(); }
    }

    private static bool BootstrapConfigured(IConfiguration config) =>
        !string.IsNullOrEmpty(config["AdminAuth:BootstrapSecret"]) &&
        DateTimeOffset.TryParse(config["AdminAuth:BootstrapExpiresUtc"], out var expiry) && expiry > DateTimeOffset.UtcNow;

    private static bool BootstrapValid(HttpContext ctx, IConfiguration config) =>
        BootstrapConfigured(config) &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Bootstrap-Secret"].ToString()),
            System.Text.Encoding.UTF8.GetBytes(config["AdminAuth:BootstrapSecret"]!));

    private sealed class AdminRow { public Guid Id { get; set; } public string Name { get; set; } = ""; }
    private sealed class CredentialRow { public byte[] CredentialId { get; set; } = []; public byte[] PublicKey { get; set; } = []; public long SignCount { get; set; } public byte[] UserHandle { get; set; } = []; public Guid Id { get; set; } public string Name { get; set; } = ""; }
}

public sealed record AdminPasskeyRegisterRequest(string AdminId);

public sealed record ChallengeState(string Options, string UserId);
public interface IDistributedCacheChallenge
{
    Task Put(HttpContext context, object options, string userId, CancellationToken ct);
    Task<ChallengeState?> Take(HttpContext context, CancellationToken ct);
}
public sealed class DistributedCacheChallenge(IDistributedCache cache) : IDistributedCacheChallenge
{
    public async Task Put(HttpContext context, object optionsObject, string userId, CancellationToken ct)
    {
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(3) };
        await cache.SetStringAsync("admin-webauthn:" + token, JsonSerializer.Serialize(new ChallengeState(JsonSerializer.Serialize(optionsObject, optionsObject.GetType(), WebJson.Options), userId)), options, ct);
        context.Response.Cookies.Append("checkpoint.webauthn", token, new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/api/admin", MaxAge = TimeSpan.FromMinutes(3) });
    }
    public async Task<ChallengeState?> Take(HttpContext context, CancellationToken ct)
    {
        var token = context.Request.Cookies["checkpoint.webauthn"];
        context.Response.Cookies.Delete("checkpoint.webauthn", new CookieOptions { Path = "/api/admin", Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict });
        if (string.IsNullOrEmpty(token)) return null;
        var key = "admin-webauthn:" + token;
        var state = await cache.GetStringAsync(key, ct);
        await cache.RemoveAsync(key, ct);
        return state is null ? null : JsonSerializer.Deserialize<ChallengeState>(state);
    }
}
internal static class WebJson { public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }; }
