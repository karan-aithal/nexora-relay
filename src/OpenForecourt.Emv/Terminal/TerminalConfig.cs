namespace OpenForecourt.Emv.Terminal;

/// <summary>
/// The terminal-resident configuration an EMV kernel needs: its own identity data, the
/// action codes that decide online/decline, and the risk-management thresholds. Values that
/// go on the wire are held as hex so they map straight onto EMV data elements.
/// </summary>
/// <remarks>
/// In a real estate these come from acquirer parameters downloaded to the terminal. Here
/// they are supplied by the caller; the demo and the tests use fixed values. All lengths
/// are the EMV lengths for the corresponding tag.
/// </remarks>
public sealed record TerminalConfig
{
    /// <summary>Terminal Country Code (tag 9F1A), hex, 2 bytes — e.g. <c>0826</c> (UK).</summary>
    public string CountryCode { get; init; } = "0826";

    /// <summary>Transaction Currency Code (tag 5F2A), hex, 2 bytes — e.g. <c>0826</c> (GBP).</summary>
    public string CurrencyCode { get; init; } = "0826";

    /// <summary>Terminal Type (tag 9F35), hex, 1 byte — e.g. <c>22</c> (attended, offline with online capability).</summary>
    public string TerminalType { get; init; } = "22";

    /// <summary>Terminal Capabilities (tag 9F33), hex, 3 bytes.</summary>
    public string TerminalCapabilities { get; init; } = "E0F8C8";

    /// <summary>Interface Device (terminal) Serial Number (tag 9F1E), hex, 8 bytes.</summary>
    public string IfdSerialNumber { get; init; } = "3132333435363738";

    /// <summary>Transaction Type (tag 9C), hex, 1 byte — <c>00</c> = purchase.</summary>
    public string TransactionType { get; init; } = "00";

    /// <summary>Terminal Action Code — Denial (5 bytes). ANDed with the TVR to force a decline.</summary>
    public string TacDenial { get; init; } = "0000000000";

    /// <summary>Terminal Action Code — Online (5 bytes). ANDed with the TVR to force going online.</summary>
    public string TacOnline { get; init; } = "F0F0F0F0F0";

    /// <summary>Terminal Action Code — Default (5 bytes). Used when the terminal cannot go online.</summary>
    public string TacDefault { get; init; } = "F0F0F0F0F0";

    /// <summary>The floor limit in the transaction's minor units. Amounts above it must go online.</summary>
    public long FloorLimitMinor { get; init; } = 5000;

    /// <summary>
    /// Random-selection target percentage (0..100) for below-floor-limit transactions.
    /// A value of 0 disables random selection; 100 always selects.
    /// </summary>
    public int RandomSelectionTargetPercent { get; init; }
}
