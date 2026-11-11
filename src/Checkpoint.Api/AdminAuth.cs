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
        app.MapGet("/api/admin/auth/csrf", (HttpContext context, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        }).RequireAuthorization().RequireCors("admin");
        if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Admin:EnableDevLogin"))
            app.MapPost("/api/admin/auth/dev-login", async (HttpContext ctx) =>
            {
                var ip = ctx.Connection.RemoteIpAddress;
                var origin = ctx.Request.Headers.Origin.ToString();
                if (ip is null || !System.Net.IPAddress.IsLoopback(ip) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host is not ("localhost" or "127.0.0.1") || uri.Port != 4321) return Results.Forbid();
                var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "5f3f0000-0000-4000-8000-000000000001"), new Claim(ClaimTypes.Name, "Admin local"), new Claim(ClaimTypes.Role, "admin") };
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
                return Results.Ok(new { success = true });
            }).RequireRateLimiting("admin-auth").RequireCors("admin");
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
            var credential = await db.QuerySingleOrDefaultAsync<CredentialRow>(new CommandDefinition("SELECT c.credential_id,c.public_key,c.sign_count,c.user_handle,u.id,u.name FROM checkpoint.admin_passkeys c JOIN checkpoint.admin_users u ON u.id=c.user_id WHERE c.credential_id=@id AND u.id='5f3f0000-0000-4000-8000-000000000001'", new { id }, cancellationToken: ct));
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

    private sealed class CredentialRow { public byte[] CredentialId { get; set; } = []; public byte[] PublicKey { get; set; } = []; public long SignCount { get; set; } public byte[] UserHandle { get; set; } = []; public Guid Id { get; set; } public string Name { get; set; } = ""; }
}

public sealed record ChallengeState(string Options, string UserId);
public interface IDistributedCacheChallenge
{
    Task Put(HttpContext context, object options, string userId, CancellationToken ct);
    Task<ChallengeState?> Take(HttpContext context, CancellationToken ct);
}
public sealed class DistributedCacheChallenge(IDistributedCache cache) : IDistributedCacheChallenge
{
    private readonly SemaphoreSlim consumeLock = new(1, 1);
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
        await consumeLock.WaitAsync(ct);
        try
        {
            var state = await cache.GetStringAsync(key, ct);
            await cache.RemoveAsync(key, ct);
            return state is null ? null : JsonSerializer.Deserialize<ChallengeState>(state);
        }
        finally { consumeLock.Release(); }
    }
}
internal static class WebJson { public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }; }
