using System.Globalization;
using System.Text;

namespace OpenForecourt.Emv.BerTlv;

/// <summary>
/// Names for the common EMV tags, so a decoded trace reads as names rather than hex. Only
/// the tags this project actually handles are listed; an unknown tag renders as its hex.
/// </summary>
/// <remarks>
/// These names are the standard EMV tag assignments (EMV 4.x Book 3, Annex A / the common
/// terminal data dictionary). They are widely published and stable. Where a tag's exact
/// name varies between sources the shortest unambiguous form is used.
/// </remarks>
public static class EmvTags
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x4F] = "Application Identifier (AID)",
        [0x50] = "Application Label",
        [0x57] = "Track 2 Equivalent Data",
        [0x5A] = "Application PAN",
        [0x61] = "Application Template (directory entry)",
        [0x6F] = "FCI Template",
        [0x70] = "Record Template",
        [0x77] = "Response Message Template Format 2",
        [0x80] = "Response Message Template Format 1",
        [0x82] = "Application Interchange Profile (AIP)",
        [0x84] = "Dedicated File (DF) Name",
        [0x87] = "Application Priority Indicator",
        [0x88] = "Short File Identifier (SFI)",
        [0x8C] = "CDOL1",
        [0x8D] = "CDOL2",
        [0x8E] = "CVM List",
        [0x94] = "Application File Locator (AFL)",
        [0x95] = "Terminal Verification Results (TVR)",
        [0x9A] = "Transaction Date",
        [0x9C] = "Transaction Type",
        [0xA5] = "FCI Proprietary Template",
        [0x5F20] = "Cardholder Name",
        [0x5F24] = "Application Expiration Date",
        [0x5F25] = "Application Effective Date",
        [0x5F28] = "Issuer Country Code",
        [0x5F2A] = "Transaction Currency Code",
        [0x5F2D] = "Language Preference",
        [0x5F34] = "PAN Sequence Number",
        [0x9F02] = "Amount, Authorised",
        [0x9F03] = "Amount, Other",
        [0x9F07] = "Application Usage Control (AUC)",
        [0x9F08] = "Application Version Number",
        [0x9F0D] = "Issuer Action Code - Default",
        [0x9F0E] = "Issuer Action Code - Denial",
        [0x9F0F] = "Issuer Action Code - Online",
        [0x9F10] = "Issuer Application Data (IAD)",
        [0x9F1A] = "Terminal Country Code",
        [0x9F1E] = "Interface Device Serial Number",
        [0x9F26] = "Application Cryptogram",
        [0x9F27] = "Cryptogram Information Data (CID)",
        [0x9F33] = "Terminal Capabilities",
        [0x9F34] = "CVM Results",
        [0x9F35] = "Terminal Type",
        [0x9F36] = "Application Transaction Counter (ATC)",
        [0x9F37] = "Unpredictable Number",
        [0x9F38] = "Processing Options Data Object List (PDOL)",
        [0x9F13] = "Last Online ATC Register",
        [0xBF0C] = "FCI Issuer Discretionary Data",
    };

    /// <summary>Returns the tag's name, or its hex if not known.</summary>
    public static string NameOf(Tag tag) =>
        Names.TryGetValue(tag.Value, out var name) ? name : tag.ToString();

    /// <summary>Tags carrying a PAN. Their values are masked in a decoded trace (CLAUDE.md section 7).</summary>
    private static readonly HashSet<uint> PanTags = [0x5A, 0x57];

    /// <summary>
    /// Renders a TLV tree as an indented, name-annotated trace.
    /// </summary>
    /// <param name="nodes">The parsed tree.</param>
    /// <param name="maskPan">
    /// When true (the default), values of PAN-bearing tags (5A, 57) are masked to first 6 +
    /// last 4. The raw APDU hex a caller prints alongside still shows the on-wire bytes; this
    /// keeps the human-readable decode from parading a PAN even in a test trace.
    /// </param>
    public static string Describe(IReadOnlyList<TlvNode> nodes, bool maskPan = true)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            Describe(node, 0, sb, maskPan);
        }

        return sb.ToString();
    }

    private static void Describe(TlvNode node, int depth, StringBuilder sb, bool maskPan)
    {
        string indent = new(' ', depth * 2);
        sb.Append(CultureInfo.InvariantCulture, $"{indent}{node.Tag} {NameOf(node.Tag)}");

        if (node.Tag.IsConstructed)
        {
            sb.Append('\n');
            foreach (var child in node.Children)
            {
                Describe(child, depth + 1, sb, maskPan);
            }
        }
        else
        {
            string hex = Convert.ToHexString(node.Value.Span);
            if (maskPan && PanTags.Contains(node.Tag.Value))
            {
                hex = MaskPanDigits(hex);
            }

            sb.Append(CultureInfo.InvariantCulture, $" = {hex}\n");
        }
    }

    /// <summary>Masks the leading digit run (the PAN) of a hex string to first 6 + last 4.</summary>
    private static string MaskPanDigits(string hex)
    {
        // Track 2 (tag 57) separates the PAN from the rest with a 'D' nibble; mask up to it.
        int end = hex.IndexOf('D', StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = hex.IndexOf('F', StringComparison.OrdinalIgnoreCase); // 5A padding
        }

        if (end < 0)
        {
            end = hex.Length;
        }

        if (end <= 10)
        {
            return hex; // too short to mask meaningfully
        }

        return string.Concat(hex.AsSpan(0, 6), new string('*', end - 10), hex.AsSpan(end - 4));
    }
}
