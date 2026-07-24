using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenForecourt.VirtualCard;

/// <summary>
/// A card's data, loaded from JSON under <c>tests/testdata/cards/</c>. This is pure card
/// data — AIDs, records, canned cryptogram — with no encoding: the <see cref="VirtualCard"/>
/// turns it into BER-TLV on the wire. Keeping the profile as values (not hex-encoded TLV)
/// means the card is genuinely data-driven and the test data cannot mis-encode a length.
/// </summary>
/// <remarks>All PANs and keys here are test-only values (CLAUDE.md section 7).</remarks>
public sealed class CardProfile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>A human-readable profile name, used in traces.</summary>
    public string Name { get; init; } = "";

    /// <summary>The interface: <c>Contact</c> or <c>Contactless</c>.</summary>
    public string Kind { get; init; } = "Contact";

    /// <summary>Whether the card answers a SELECT of the PSE (<c>1PAY.SYS.DDF01</c>).</summary>
    public bool SupportsPse { get; init; }

    /// <summary>Whether the card answers a SELECT of the PPSE (<c>2PAY.SYS.DDF01</c>).</summary>
    public bool SupportsPpse { get; init; } = true;

    /// <summary>The SFI holding the PSE application directory (records of tag-61 entries).</summary>
    public int PseDirectorySfi { get; init; } = 1;

    /// <summary>The applications this card carries, in the card's own order.</summary>
    public IReadOnlyList<CardApplication> Applications { get; init; } = [];

    /// <summary>Loads and validates a profile from a JSON file.</summary>
    public static CardProfile Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses a profile from a JSON string.</summary>
    public static CardProfile Parse(string json)
    {
        var profile = JsonSerializer.Deserialize<CardProfile>(json, Options)
            ?? throw new FormatException("Card profile JSON was null.");
        if (profile.Applications.Count == 0)
        {
            throw new FormatException($"Card profile '{profile.Name}' has no applications.");
        }

        return profile;
    }
}

/// <summary>One application (AID) on a card.</summary>
public sealed class CardApplication
{
    /// <summary>The Application Identifier, hex (tag 4F / 84 / DF name).</summary>
    public string Aid { get; init; } = "";

    /// <summary>The application label shown to a cardholder (tag 50).</summary>
    public string Label { get; init; } = "";

    /// <summary>Selection priority; 1 is highest (tag 87).</summary>
    public int Priority { get; init; } = 1;

    /// <summary>The PDOL (tag 9F38): what the terminal must supply in GET PROCESSING OPTIONS.</summary>
    public IReadOnlyList<DolEntry> Pdol { get; init; } = [];

    /// <summary>Application Interchange Profile, hex, 2 bytes (tag 82).</summary>
    public string Aip { get; init; } = "0000";

    /// <summary>Application File Locator, hex, a multiple of 4 bytes (tag 94).</summary>
    public string Afl { get; init; } = "";

    /// <summary>Record contents keyed by <c>"sfi:record"</c>; each is a tag→hex-value map wrapped in a 70 template.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, string>> Records { get; init; }
        = new Dictionary<string, Dictionary<string, string>>();

    /// <summary>GET DATA responses keyed by tag hex (e.g. <c>9F36</c> ATC, <c>9F13</c> last online ATC).</summary>
    public IReadOnlyDictionary<string, string> GetData { get; init; } = new Dictionary<string, string>();

    /// <summary>The canned GENERATE AC response.</summary>
    public GenerateAcProfile GenerateAc { get; init; } = new();
}

/// <summary>One entry of a Data Object List: a tag and the length the terminal must supply.</summary>
public sealed class DolEntry
{
    /// <summary>The requested tag, hex.</summary>
    public string Tag { get; init; } = "";

    /// <summary>The number of value bytes expected.</summary>
    public int Length { get; init; }
}

/// <summary>The card's fixed GENERATE AC answer (test data — a real card would compute the cryptogram).</summary>
public sealed class GenerateAcProfile
{
    /// <summary>Cryptogram Information Data (tag 9F27): 80 = ARQC, 40 = TC, 00 = AAC.</summary>
    public string Cid { get; init; } = "80";

    /// <summary>The Application Cryptogram (tag 9F26), hex, 8 bytes.</summary>
    public string Cryptogram { get; init; } = "0000000000000000";

    /// <summary>Issuer Application Data (tag 9F10), hex.</summary>
    public string Iad { get; init; } = "";

    /// <summary>The starting Application Transaction Counter (tag 9F36), hex, 2 bytes.</summary>
    public string Atc { get; init; } = "0000";
}
