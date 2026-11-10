using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Checkpoint.Tests;

[Collection("PostgreSQL")]
public sealed class CorsTests(ApiFixture fixture)
{
    [Fact]
    public async Task Development_allows_any_origin_and_preflight_for_required_headers()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/photos");
        request.Headers.Add("Origin", "https://frontend.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-visitor-id");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        var headers = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("X-Visitor-Id", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Type", headers, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Development_exposes_retry_after_on_service_error()
    {
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Checkpoint"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;Pooling=false",
                ["Database:MigrateOnStartup"] = "false",
                ["Database:SeedDemo"] = "false"
            })));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        request.Headers.Add("Origin", "https://any-origin.example");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Contains("Retry-After", string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_allows_only_configured_origins_and_exposes_retry_after_on_503()
    {
        using var factory = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Checkpoint"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;Pooling=false",
                ["Database:MigrateOnStartup"] = "false",
                ["Database:SeedDemo"] = "false",
                ["Cors:Origins:0"] = "https://frontend.example"
            }));
        });
        using var client = factory.CreateClient();
        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        allowed.Headers.Add("Origin", "https://frontend.example");
        using var unavailable = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("https://frontend.example", Assert.Single(unavailable.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("5", Assert.Single(unavailable.Headers.GetValues("Retry-After")));
        Assert.Contains("Retry-After", string.Join(",", unavailable.Headers.GetValues("Access-Control-Expose-Headers")), StringComparison.OrdinalIgnoreCase);

        using var denied = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        denied.Headers.Add("Origin", "https://other.example");
        using var response = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
