using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenForecourt.HostSimulator;

/// <summary>
/// The acquirer simulator's behaviour, loaded from JSON so a demo or a test can change what
/// the "issuer" does without a rebuild.
/// </summary>
/// <remarks>
/// This is what makes the simulator useful: a terminal cannot be tested properly against a
/// host that only ever approves. Declines, slow responses and total silence are the
/// interesting cases, and they must be reproducible on demand.
/// </remarks>
public sealed record HostRules
{
    /// <summary>Amounts strictly below this (in minor units) are approved; at or above it, declined 51.</summary>
    public long ApproveBelowMinor { get; init; } = 10_000;

    /// <summary>PAN to response code, e.g. <c>"4000000000000051": "51"</c>. Takes priority over the amount rule.</summary>
    public IReadOnlyDictionary<string, string> DeclineByPan { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>PANs for which the host stays silent, so terminal timeout handling can be exercised.</summary>
    public IReadOnlyList<string> NoResponsePans { get; init; } = [];

    /// <summary>Latency applied to every response, in milliseconds.</summary>
    public int LatencyMs { get; init; }

    /// <summary>Extra latency for specific PANs, in milliseconds, added to <see cref="LatencyMs"/>.</summary>
    public IReadOnlyDictionary<string, int> ExtraLatencyByPanMs { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads rules from a JSON file.</summary>
    /// <exception cref="JsonException">The file is not valid JSON for these rules.</exception>
    public static HostRules Load(string path) =>
        JsonSerializer.Deserialize<HostRules>(File.ReadAllText(path), Options)
        ?? throw new JsonException($"'{path}' contained JSON null.");

    /// <summary>Serialises the rules verbatim, for persistence — not for logging (contains PANs).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// A human summary safe to print: PAN-keyed rules are masked, so echoing the loaded
    /// configuration to a log never leaks a PAN-shaped digit sequence (CLAUDE.md section 7).
    /// </summary>
    public string Describe()
    {
        static string Mask(string pan) => new OpenForecourt.Abstractions.Domain.Pan(pan).ToString();

        string declines = DeclineByPan.Count == 0
            ? "none"
            : string.Join(", ", DeclineByPan.Select(kv => $"{Mask(kv.Key)}->{kv.Value}"));
        string silent = NoResponsePans.Count == 0
            ? "none"
            : string.Join(", ", NoResponsePans.Select(Mask));

        return $"approveBelow={ApproveBelowMinor} minor units; declineByPan=[{declines}]; " +
               $"noResponse=[{silent}]; latency={LatencyMs}ms";
    }
}
