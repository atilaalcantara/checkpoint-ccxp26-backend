using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Checkpoint.Tests;

[Collection("PostgreSQL")]
public sealed class ProgressionConfigurationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Configured_thresholds_drive_current_previous_and_final_levels_over_http()
    {
        var original = await fixture.Query<long>("SELECT total_xp FROM checkpoint.community_progress WHERE id=1");
        try
        {
            using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Progression:FriendsAtXp"] = "2",
                    ["Progression:DatingAtXp"] = "4",
                    ["Progression:EngagedAtXp"] = "8",
                    ["Progression:MarriedAtXp"] = "20"
                })));
            using var client = factory.CreateClient();
            await fixture.Execute("UPDATE checkpoint.community_progress SET total_xp=9 WHERE id=1");
            var response = JsonNode.Parse(await client.GetStringAsync("/api/v1/progress"))!;
            Assert.Equal("engaged", (string?)response["currentLevel"]!["id"]);
            Assert.Equal(8, (int)response["currentLevel"]!["minimumXp"]!);
            Assert.Equal(20, (int)response["currentLevel"]!["nextLevelAtXp"]!);
            Assert.Equal(new[] { 0, 2, 4 }, response["previousLevels"]!.AsArray().Select(level => (int)level!["minimumXp"]!));

            await fixture.Execute("UPDATE checkpoint.community_progress SET total_xp=20 WHERE id=1");
            var final = JsonNode.Parse(await client.GetStringAsync("/api/v1/progress"))!["currentLevel"]!;
            Assert.Equal("married", (string?)final["id"]);
            Assert.True((bool)final["isFinal"]!);
            Assert.Null(final["nextLevelAtXp"]);
        }
        finally { await fixture.Execute("UPDATE checkpoint.community_progress SET total_xp=@original WHERE id=1", new { original }); }
    }

    [Fact]
    public void Invalid_thresholds_prevent_application_startup()
    {
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Progression:MarriedAtXp"] = "7" })));
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("FriendsAtXp < DatingAtXp < EngagedAtXp < MarriedAtXp", error.Message);
    }
}
