namespace OpenForecourt.Abstractions.Domain;

/// <summary>
/// A Primary Account Number (card number).
/// </summary>
/// <remarks>
/// <para>
/// This type is the enforcement point for CLAUDE.md section 7: cleartext PANs must never
/// leak into logs. It holds the digits privately and its <see cref="ToString"/> returns
/// the <b>masked</b> form (first 6 + last 4, the rest as <c>*</c>). Any code that needs the
/// raw value must call <see cref="Reveal"/> explicitly, so every unmasking is greppable
/// (search the codebase for <c>.Reveal()</c> to audit every place a clear PAN is touched).
/// </para>
/// <para>
/// Masking policy: at most the first 6 (IIN/BIN) and last 4 digits are ever exposed by
/// <see cref="ToString"/>. For short PANs the overlap is clamped so no more than the whole
/// number, and never both ends overlapping, is shown.
/// </para>
/// </remarks>
public readonly struct Pan
{
    private readonly string _digits;

    /// <summary>Wraps a PAN. Only test PANs are permitted in this repository (CLAUDE.md section 7).</summary>
    /// <param name="digits">The account number digits. Whitespace is not trimmed; pass clean digits.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="digits"/> is null, empty, or not all digits.</exception>
    public Pan(string digits)
    {
        if (string.IsNullOrEmpty(digits))
        {
            throw new ArgumentException("PAN must not be empty.", nameof(digits));
        }

        foreach (char c in digits)
        {
            if (c is < '0' or > '9')
            {
                throw new ArgumentException("PAN must contain digits only.", nameof(digits));
            }
        }

        _digits = digits;
    }

    /// <summary>The number of digits in the PAN.</summary>
    public int Length => _digits.Length;

    /// <summary>
    /// Returns the raw, unmasked PAN. This is the ONLY way to obtain cleartext digits;
    /// every call site is an auditable point where a clear PAN is handled.
    /// </summary>
    public string Reveal() => _digits;

    /// <summary>
    /// Returns the masked PAN: at most the first 6 and last 4 digits, everything else
    /// replaced with <c>*</c>. This is what any log, event, or diagnostic will see.
    /// </summary>
    public override string ToString()
    {
        int len = _digits.Length;

        // Show first up-to-6 and last up-to-4, but never let the two windows overlap.
        int lead = Math.Min(6, len);
        int trail = Math.Min(4, len - lead);
        int masked = len - lead - trail;

        if (masked <= 0)
        {
            // PAN too short to mask anything meaningfully; hide all but the last digit.
            return new string('*', Math.Max(0, len - 1)) + (len > 0 ? _digits[^1].ToString() : string.Empty);
        }

        return string.Concat(
            _digits.AsSpan(0, lead),
            new string('*', masked),
            _digits.AsSpan(len - trail, trail));
    }
}
