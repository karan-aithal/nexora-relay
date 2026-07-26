using Microsoft.Extensions.Logging;
using OpenForecourt.SiteController.Config;
using OpenForecourt.VirtualCard;

namespace OpenForecourt.SiteController.Opt;

/// <summary>A card the simulator offers, as the dashboard sees it.</summary>
/// <param name="Id">The profile file's stem, used to select it.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">Contact, Contactless, …</param>
/// <param name="Brand">Card brand inferred from the primary AID.</param>
public sealed record CardProfileView(string Id, string Name, string Kind, string Brand);

/// <summary>
/// The test card profiles the card simulator can present. Loaded once from
/// <see cref="SiteOptions.CardProfilesPath"/>; these are the same JSON profiles the Phase 2
/// virtual card and its tests use, so the card the dashboard presents is the card the EMV unit
/// tests exercise. Test PANs only (CLAUDE.md section 7.1).
/// </summary>
public sealed class CardCatalogue
{
    private readonly Dictionary<string, CardProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads every <c>*.json</c> profile in the configured directory.</summary>
    public CardCatalogue(SiteOptions options, ILogger<CardCatalogue> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        string directory = options.CardProfilesPath;
        if (!Directory.Exists(directory))
        {
            logger.LogWarning("No card profiles at '{Directory}'; the card simulator will be empty.", directory);
            Views = [];
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                _profiles[Path.GetFileNameWithoutExtension(path)] = CardProfile.Load(path);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                logger.LogWarning(ex, "Skipping unreadable card profile '{Path}'.", path);
            }
        }

        Views = [.. _profiles.Select(kv => new CardProfileView(kv.Key, kv.Value.Name, kv.Value.Kind, Brand(kv.Value)))];
        logger.LogInformation("Card simulator loaded {Count} profiles from '{Directory}'.", _profiles.Count, directory);
    }

    /// <summary>The catalogue, for the card simulator's picker.</summary>
    public IReadOnlyList<CardProfileView> Views { get; }

    /// <summary>Returns a profile by id, or null.</summary>
    public CardProfile? TryGet(string id) => _profiles.TryGetValue(id, out var profile) ? profile : null;

    /// <summary>Returns the brand for a profile id, or <c>"Unknown"</c>.</summary>
    public string BrandOf(string id) => _profiles.TryGetValue(id, out var profile) ? Brand(profile) : "Unknown";

    // Registered Application Provider Identifier: the first five bytes of the AID identify the
    // payment scheme. Enough to label a tile; a real terminal matches the full AID list.
    private static string Brand(CardProfile profile) =>
        profile.Applications.Count == 0 ? "Unknown" : profile.Applications[0].Aid switch
        {
            var aid when aid.StartsWith("A000000003", StringComparison.OrdinalIgnoreCase) => "Visa",
            var aid when aid.StartsWith("A000000004", StringComparison.OrdinalIgnoreCase) => "Mastercard",
            var aid when aid.StartsWith("A000000025", StringComparison.OrdinalIgnoreCase) => "Amex",
            _ => "Unknown",
        };
}
