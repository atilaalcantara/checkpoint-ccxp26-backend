using System.Text.Json;
using Dapper;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Checkpoint.Api;

public static class HomologationPasskeys
{
    public const string Host = "ccxp26-hlg.atilaalcantara.com";
    public const string Flag = "Admin:EnablePublicPasskeyRegistration";

    public static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsEnvironment("Homologation") && configuration.GetValue<bool>(Flag);

    public static void Validate(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(Flag)) return;
        if (!Enabled(environment, configuration) || configuration["Fido2:ServerDomain"] != Host ||
            configuration.GetSection("Fido2:Origins").Get<string[]>() is not { Length: 1 } origins ||
            origins[0] != $"https://{Host}")
            throw new InvalidOperationException("Public passkey registration requires Homologation and the dedicated HTTPS HLG relying party.");
    }

    public static void MapHomologationPasskeys(this WebApplication app)
    {
        Validate(app.Environment, app.Configuration);
        app.MapGet("/api/admin/auth/config", () => Results.Ok(new
        {
            publicPasskeyRegistrationEnabled = Enabled(app.Environment, app.Configuration)
        })).RequireRateLimiting("admin-auth").RequireCors("admin");
        if (!Enabled(app.Environment, app.Configuration)) return;
        app.MapPost("/api/admin/passkeys/register/options", Options).RequireRateLimiting("admin-auth").RequireCors("admin");
        app.MapPost("/api/admin/passkeys/register/verify", Verify).RequireRateLimiting("admin-auth").RequireCors("admin");
    }

    private static async Task<IResult> Options(HttpContext ctx, IDistributedCacheChallenge challenges, [FromServices] IFido2 fido, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var name = $"HLG-{id:N}";
        var options = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = id.ToByteArray(), Name = name, DisplayName = name },
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None
        });
        await challenges.Put(ctx, options, id.ToString(), ct);
        return Results.Ok(options);
    }

    private static async Task<IResult> Verify(HttpContext ctx, Store store, IDistributedCacheChallenge challenges, [FromServices] IFido2 fido, [FromBody] AuthenticatorAttestationRawResponse response, CancellationToken ct)
    {
        var state = await challenges.Take(ctx, ct);
        if (state is null || !Guid.TryParse(state.UserId, out var id))
            return Results.BadRequest(new { error = "Challenge expirado. Solicite novamente." });
        try
        {
            var options = JsonSerializer.Deserialize<CredentialCreateOptions>(state.Options, WebJson.Options);
            if (options is null || !options.User.Id.SequenceEqual(id.ToByteArray())) return Results.BadRequest();
            var credential = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                {
                    await using var connection = await store.Open(token);
                    return !await Store.One<bool>(connection, "SELECT EXISTS(SELECT 1 FROM checkpoint.admin_passkeys WHERE credential_id=@id)", new { id = args.CredentialId }, token);
                }
            }, ct);
            await using var db = await store.Open(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            await Store.Execute(db, "INSERT INTO checkpoint.admin_users(id,name) VALUES(@id,@name)", new { id, name = options.User.Name }, ct);
            await Store.Execute(db, """
                INSERT INTO checkpoint.admin_passkeys(credential_id,user_id,public_key,sign_count,user_handle)
                VALUES(@credentialId,@id,@publicKey,@counter,@handle)
                """, new { credentialId = credential.Id, id, publicKey = credential.PublicKey, counter = (long)credential.SignCount, handle = options.User.Id }, ct);
            await tx.CommitAsync(ct);
            return Results.Json(new { success = true }, statusCode: 201);
        }
        catch (Fido2VerificationException) { return Results.BadRequest(new { error = "Passkey inválida. Solicite novamente." }); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Results.Conflict(new { error = "Passkey já cadastrada." }); }
    }
}
