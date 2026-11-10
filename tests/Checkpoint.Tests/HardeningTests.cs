using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Checkpoint.Tests;

[Collection("PostgreSQL")]
public sealed class HardeningTests(ApiFixture fixture)
{
    private static HttpContent Body(string path) => path.EndsWith("votes") ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json") : new MultipartFormDataContent();

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var response = await fixture.Client.GetAsync("/health/live");
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task Production_sends_csp_and_rate_limits_uploads_and_votes_but_not_health()
    {
        using var factory = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Checkpoint"] = fixture.ConnectionString,
                ["Database:MigrateOnStartup"] = "false",
                ["Database:SeedDemo"] = "false",
                ["Cors:Origins:0"] = "https://frontend.example",
                ["RateLimit:UploadsPerMinute"] = "2",
                ["RateLimit:VotesPerMinute"] = "2"
            }));
        });
        using var client = factory.CreateClient();
        Assert.Equal(2, ((Checkpoint.Api.RateLimitSettings)factory.Services.GetService(typeof(Checkpoint.Api.RateLimitSettings))!).VotesPerMinute);
        using var health = await client.GetAsync("/health/live");
        Assert.Contains("frame-ancestors 'none'", Assert.Single(health.Headers.GetValues("Content-Security-Policy")));
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        foreach (var path in new[] { "/api/v1/votes", "/api/v1/photos" })
        {
            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < 4; i++) codes.Add((await client.PostAsync(path, Body(path))).StatusCode);
            Assert.Contains(HttpStatusCode.TooManyRequests, codes);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, codes[0]);
        }
    }
}
