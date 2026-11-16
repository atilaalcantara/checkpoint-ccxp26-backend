using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Checkpoint.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Checkpoint.Tests;

public sealed class HomologationPasskeyTests
{
    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(bool enabled, string host = HomologationPasskeys.Host, string? origin = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HomologationPasskeys.Flag] = enabled.ToString(),
            ["Fido2:ServerDomain"] = host,
            ["Fido2:Origins:0"] = origin ?? $"https://{host}"
        }).Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void Explicit_flag_cannot_enable_registration_outside_homologation(string environment)
    {
        var env = new EnvironmentStub(environment);
        Assert.False(HomologationPasskeys.Enabled(env, Config(true)));
        Assert.Throws<InvalidOperationException>(() => HomologationPasskeys.Validate(env, Config(true)));
        HomologationPasskeys.Validate(env, Config(false));
    }

    [Theory]
    [InlineData("localhost", "https://ccxp26-hlg.atilaalcantara.com")]
    [InlineData("ccxp26-hlg.atilaalcantara.com", "http://ccxp26-hlg.atilaalcantara.com")]
    [InlineData("ccxp26-hlg.atilaalcantara.com", "https://production.example")]
    public void Registration_rejects_wrong_relying_party(string host, string origin) =>
        Assert.Throws<InvalidOperationException>(() => HomologationPasskeys.Validate(new EnvironmentStub("Homologation"), Config(true, host, origin)));

    internal static WebApplicationFactory<Program> App(string environment, bool enabled, string? connectionString = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HomologationPasskeys.Flag] = enabled.ToString(),
                ["ConnectionStrings:Checkpoint"] = connectionString ?? "Host=localhost;Database=unused;Username=unused",
                ["Fido2:ServerDomain"] = HomologationPasskeys.Host,
                ["Fido2:Origins:0"] = $"https://{HomologationPasskeys.Host}",
                ["Cors:Origins:0"] = $"https://{HomologationPasskeys.Host}",
                ["Database:MigrateOnStartup"] = "false",
                ["Database:SeedDemo"] = "false"
            }));
        });

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", false)]
    [InlineData("Homologation", false)]
    [InlineData("Homologation", true)]
    public async Task Configuration_and_routes_follow_environment_and_explicit_flag(string environment, bool enabled)
    {
        await using var app = App(environment, enabled);
        using var client = app.CreateClient(new() { BaseAddress = new Uri($"https://{HomologationPasskeys.Host}") });
        var response = await client.GetAsync("/api/admin/auth/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var config = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(enabled, config.GetProperty("publicPasskeyRegistrationEnabled").GetBoolean());
        var options = await client.PostAsync("/api/admin/passkeys/register/options", null);
        Assert.Equal(enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, options.StatusCode);
        if (enabled)
        {
            var json = await options.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ccxp26-hlg.atilaalcantara.com", json.GetProperty("rp").GetProperty("id").GetString());
            Assert.Equal("required", json.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());
            Assert.Equal("required", json.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
            // Registration remains available across successive requests; no bootstrap latch.
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/passkeys/register/options", null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Public_registration_shares_existing_auth_rate_limit()
    {
        await using var app = App("Homologation", true);
        using var client = app.CreateClient(new() { BaseAddress = new Uri($"https://{HomologationPasskeys.Host}") });
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/passkeys/register/options", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/admin/auth/passkey/options", null)).StatusCode);
    }

    [Fact]
    public async Task Registration_requires_its_own_single_use_challenge()
    {
        await using var app = App("Homologation", true);
        using var client = app.CreateClient(new() { BaseAddress = new Uri($"https://{HomologationPasskeys.Host}") });
        var payload = new { id = "AQ", rawId = "AQ", type = "public-key", response = new { clientDataJSON = "e30", attestationObject = "oA" }, clientExtensionResults = new { } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/passkeys/register/verify", payload)).StatusCode);
        await client.PostAsync("/api/admin/auth/passkey/options", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/passkeys/register/verify", payload)).StatusCode);
        await client.PostAsync("/api/admin/passkeys/register/options", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/passkeys/register/verify", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/passkeys/register/verify", payload)).StatusCode);
    }
}
