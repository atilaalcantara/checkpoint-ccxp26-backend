using System.Formats.Cbor;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Checkpoint.Api;
using Xunit;

namespace Checkpoint.Tests;

[Collection("PostgreSQL")]
public sealed class HomologationPasskeyPersistenceTests(ApiFixture fixture)
{
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Theory]
    [InlineData(null)]
    [InlineData("origin")]
    [InlineData("rpId")]
    [InlineData("challenge")]
    [InlineData("userVerification")]
    [InlineData("userPresence")]
    [InlineData("type")]
    public async Task Registration_validates_attestation_before_persisting_and_valid_admins_can_login(string? invalidField)
    {
        await using var app = HomologationPasskeyTests.App("Homologation", true, fixture.ConnectionString);
        for (var index = 0; index < 2; index++)
        {
            using var client = app.CreateClient(new() { BaseAddress = new Uri($"https://{HomologationPasskeys.Host}") });
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var credentialId = RandomNumberGenerator.GetBytes(32);
            var optionsResponse = await client.PostAsync("/api/admin/passkeys/register/options", null);
            Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
            var options = await optionsResponse.Content.ReadFromJsonAsync<JsonElement>();
            var user = options.GetProperty("user");
            var name = user.GetProperty("name").GetString()!;
            var handle = user.GetProperty("id").GetString()!;
            var clientData = JsonSerializer.SerializeToUtf8Bytes(new {
                type = invalidField == "type" ? "webauthn.get" : "webauthn.create",
                challenge = invalidField == "challenge" ? Encode(RandomNumberGenerator.GetBytes(32)) : options.GetProperty("challenge").GetString(),
                origin = invalidField == "origin" ? "https://production.example" : $"https://{HomologationPasskeys.Host}",
                crossOrigin = false
            });
            var flags = invalidField switch { "userVerification" => (byte)0x41, "userPresence" => (byte)0x44, _ => (byte)0x45 };
            var authData = SHA256.HashData(Encoding.UTF8.GetBytes(invalidField == "rpId" ? "production.example" : HomologationPasskeys.Host))
                .Concat(new byte[] { flags, 0, 0, 0, 0 }) // UP, UV, AT and counter
                .Concat(new byte[16]) // AAGUID
                .Concat(new byte[] { 0, (byte)credentialId.Length }).Concat(credentialId).Concat(PublicKey(key)).ToArray();
            var cbor = new CborWriter();
            cbor.WriteStartMap(3);
            cbor.WriteTextString("fmt"); cbor.WriteTextString("none");
            cbor.WriteTextString("attStmt"); cbor.WriteStartMap(0); cbor.WriteEndMap();
            cbor.WriteTextString("authData"); cbor.WriteByteString(authData); cbor.WriteEndMap();
            var attestation = new { id = Encode(credentialId), rawId = Encode(credentialId), type = "public-key", clientExtensionResults = new { }, response = new { clientDataJSON = Encode(clientData), attestationObject = Encode(cbor.Encode()) } };
            var registered = await client.PostAsJsonAsync("/api/admin/passkeys/register/verify", attestation);
            if (invalidField is not null)
            {
                Assert.Equal(HttpStatusCode.BadRequest, registered.StatusCode);
                Assert.Equal(0, await fixture.Query<int>("SELECT count(*)::int FROM checkpoint.admin_users WHERE name=@name", new { name }));
                Assert.Equal(0, await fixture.Query<int>("SELECT count(*)::int FROM checkpoint.admin_passkeys WHERE credential_id=@id", new { id = credentialId }));
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/passkeys/register/verify", attestation)).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/auth/me")).StatusCode);
                return;
            }
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/auth/me")).StatusCode);
            Assert.Equal(1, await fixture.Query<int>("SELECT count(*)::int FROM checkpoint.admin_users u JOIN checkpoint.admin_passkeys p ON p.user_id=u.id WHERE u.name=@name", new { name }));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/passkeys/register/verify", attestation)).StatusCode);

            var loginOptions = await (await client.PostAsync("/api/admin/auth/passkey/options", null)).Content.ReadFromJsonAsync<JsonElement>();
            var assertionData = JsonSerializer.SerializeToUtf8Bytes(new { type = "webauthn.get", challenge = loginOptions.GetProperty("challenge").GetString(), origin = $"https://{HomologationPasskeys.Host}", crossOrigin = false });
            var assertionAuth = SHA256.HashData(Encoding.UTF8.GetBytes(HomologationPasskeys.Host)).Concat(new byte[] { 0x05, 0, 0, 0, 1 }).ToArray();
            var signature = key.SignData(assertionAuth.Concat(SHA256.HashData(assertionData)).ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var assertion = new { id = Encode(credentialId), rawId = Encode(credentialId), type = "public-key", clientExtensionResults = new { }, response = new { clientDataJSON = Encode(assertionData), authenticatorData = Encode(assertionAuth), signature = Encode(signature), userHandle = handle } };
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/admin/auth/passkey/verify", assertion)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/auth/me")).StatusCode);
            Assert.Equal(1L, await fixture.Query<long>("SELECT sign_count FROM checkpoint.admin_passkeys WHERE credential_id=@id", new { id = credentialId }));
            // Existing admin CSRF protection still applies after a passkey login.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/admin/auth/logout", null)).StatusCode);
        }
    }

    private static byte[] PublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var writer = new CborWriter();
        writer.WriteStartMap(5);
        writer.WriteInt32(1); writer.WriteInt32(2);
        writer.WriteInt32(3); writer.WriteInt32(-7);
        writer.WriteInt32(-1); writer.WriteInt32(1);
        writer.WriteInt32(-2); writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3); writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();
        return writer.Encode();
    }
}
