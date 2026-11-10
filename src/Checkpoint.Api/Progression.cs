using NetVips;
using Microsoft.Extensions.Options;

namespace Checkpoint.Api;

public sealed record Artwork(string Role, string File);
public sealed record Level(string Id, string Name, int MinimumXp, Artwork[] Artwork);
public sealed class ProgressionSettings
{
    public int FriendsAtXp { get; set; } = 15;
    public int DatingAtXp { get; set; } = 55;
    public int EngagedAtXp { get; set; } = 130;
    public int MarriedAtXp { get; set; } = 230;

    public bool IsValid() => FriendsAtXp > 0 && FriendsAtXp < DatingAtXp &&
        DatingAtXp < EngagedAtXp && EngagedAtXp < MarriedAtXp;
}

public sealed class Progression
{
    public Level[] Levels { get; }

    public Progression(IOptions<ProgressionSettings> settings)
    {
        var thresholds = settings.Value;
        if (!thresholds.IsValid()) throw new OptionsValidationException(nameof(ProgressionSettings), typeof(ProgressionSettings),
            ["Progression thresholds must satisfy 0 < FriendsAtXp < DatingAtXp < EngagedAtXp < MarriedAtXp."]);
        Levels =
    [
        new("strangers", "Desconhecidos", 0, [new("juh", "Nivel1juhDesconhecidos.d7a24a3c38.avif"), new("atila", "Nivel1atilaDesconhecidos.cf99614b8f.avif")]),
        new("friends", "Amigos", thresholds.FriendsAtXp, [new("juh", "Nivel2juhAmigos.62679a0d06.avif"), new("atila", "Nivel2atilaAmigos.22c87e4762.avif")]),
        new("dating", "Namorados", thresholds.DatingAtXp, [new("couple", "Nivel3atilajuhNamorados.d4f2abe27a.avif")]),
        new("engaged", "Noivos", thresholds.EngagedAtXp, [new("couple", "Nivel4atilajuhNoivos.2ab8af205c.avif")]),
        new("married", "Casados", thresholds.MarriedAtXp, [new("couple", "Nivel5atilajuhCasamento.4f6042831a.avif")])
    ];
    }

    public int Index(long xp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(xp);
        return Array.FindLastIndex(Levels, level => xp >= level.MinimumXp);
    }
}

public sealed class ArtworkCatalog(IWebHostEnvironment environment, Progression progression)
{
    private readonly Dictionary<string, ArtworkCharacter[]> characters = [];
    private readonly Dictionary<string, string> versions = [];

    public void Load()
    {
        foreach (var level in progression.Levels)
        {
            var hashes = new List<string>();
            characters[level.Id] = level.Artwork.Select(art =>
            {
                var bytes = File.ReadAllBytes(Path.Combine(environment.ContentRootPath, "PrivateAssets", art.File));
                using var image = Image.NewFromBuffer(bytes);
                if (!image.HasAlpha()) throw new InvalidOperationException("Artwork must contain alpha.");
                hashes.Add(ParticipationService.Hash(bytes));
                return new ArtworkCharacter(art.Role, "image/avif", image.Width, image.Height, Convert.ToBase64String(bytes));
            }).ToArray();
            versions[level.Id] = $"{level.Id}-{ParticipationService.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join(':', hashes)))[..16]}";
        }
    }

    public CurrentLevelResponse Current(long xp)
    {
        if (characters.Count != progression.Levels.Length)
            throw new ApiException(503, "SERVICE_UNAVAILABLE", "Arte temporariamente indisponível.");
        var index = progression.Index(xp);
        var level = progression.Levels[index];
        return new(level.Id, level.Name, level.MinimumXp,
            index + 1 < progression.Levels.Length ? progression.Levels[index + 1].MinimumXp : null,
            index == progression.Levels.Length - 1, versions[level.Id], characters[level.Id]);
    }

    public PreviousLevelResponse[] Previous(long xp) => progression.Levels.Take(progression.Index(xp))
        .Select(level => new PreviousLevelResponse(level.Id, level.Name, level.MinimumXp)).ToArray();
}
