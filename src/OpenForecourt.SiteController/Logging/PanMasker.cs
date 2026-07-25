using System.Text.RegularExpressions;

namespace OpenForecourt.SiteController.Logging;

/// <summary>
/// Masks PAN-shaped digit sequences to first-6 + last-4 (CLAUDE.md section 7.3). Applied at
/// the log sink so no log call can bypass it.
/// </summary>
public static partial class PanMasker
{
    /// <summary>
    /// Replaces every 13–19 digit run in <paramref name="text"/> with its first six and last
    /// four digits, the middle turned to asterisks.
    /// </summary>
    public static string Mask(string text) =>
        string.IsNullOrEmpty(text) ? text : PanPattern().Replace(text, static m =>
        {
            string digits = m.Value;
            return string.Concat(digits[..6], new string('*', digits.Length - 10), digits[^4..]);
        });

    // A card number is 13–19 digits. Word boundaries keep it from eating the middle of a
    // longer digit blob (e.g. a hash), which would otherwise leave a PAN-length tail unmasked.
    [GeneratedRegex(@"(?<!\d)\d{13,19}(?!\d)")]
    private static partial Regex PanPattern();
}
