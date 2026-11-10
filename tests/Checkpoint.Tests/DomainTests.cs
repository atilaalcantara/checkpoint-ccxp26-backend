using Checkpoint.Api;
using Microsoft.Extensions.Options;
using Xunit;

namespace Checkpoint.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Default_thresholds_are_valid_for_a_clean_deployment()
    {
        var progression = new Progression(Options.Create(new ProgressionSettings()));
        Assert.Equal(new[] { 15, 55, 130, 230 }, progression.Levels.Skip(1).Select(level => level.MinimumXp));
    }

    [Theory]
    [InlineData(0, "strangers")]
    [InlineData(2, "strangers")]
    [InlineData(3, "friends")]
    [InlineData(6, "friends")]
    [InlineData(7, "dating")]
    [InlineData(9, "dating")]
    [InlineData(12, "engaged")]
    [InlineData(18, "engaged")]
    [InlineData(19, "married")]
    [InlineData(10000, "married")]
    public void Accumulated_xp_selects_the_correct_level_even_across_multiple_thresholds(long xp, string id)
    {
        var progression = new Progression(Options.Create(new ProgressionSettings
        {
            FriendsAtXp = 3,
            DatingAtXp = 7,
            EngagedAtXp = 12,
            MarriedAtXp = 19
        }));
        Assert.Equal(id, progression.Levels[progression.Index(xp)].Id);
    }

    [Theory]
    [InlineData(0, 7, 12, 19)]
    [InlineData(3, 3, 12, 19)]
    [InlineData(3, 7, 6, 19)]
    [InlineData(3, 7, 12, 12)]
    public void Invalid_thresholds_are_rejected(int friends, int dating, int engaged, int married) =>
        Assert.Throws<OptionsValidationException>(() => new Progression(Options.Create(new ProgressionSettings
        {
            FriendsAtXp = friends,
            DatingAtXp = dating,
            EngagedAtXp = engaged,
            MarriedAtXp = married
        })));

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("11111111-1111-1111-8111-111111111111")]
    [InlineData("11111111-1111-4111-7111-111111111111")]
    public void Visitor_must_be_canonical_uuid_v4(string value) => Assert.Throws<ApiException>(() => Headers.Parse(value, "INVALID_VISITOR_ID"));
}
