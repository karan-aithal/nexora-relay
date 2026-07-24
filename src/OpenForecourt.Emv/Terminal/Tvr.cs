namespace OpenForecourt.Emv.Terminal;

/// <summary>
/// The Terminal Verification Results — the five-byte record (EMV tag <c>95</c>) of every
/// check the terminal ran and how each turned out. It is the terminal's memory of the
/// transaction: processing restrictions, cardholder verification, risk management and
/// terminal action analysis all write bits here, and it is fed back to the card in
/// GENERATE AC and to the issuer in field 55.
/// </summary>
/// <remarks>
/// The bit positions are fixed by EMV 4.x Book 3, Annex C5. Only the bits this terminal
/// actually sets are named; the rest stay zero. Bit numbering follows the spec: byte 1 is
/// the first (most significant) byte, bit 8 the most significant bit of a byte.
/// </remarks>
public sealed class Tvr
{
    private readonly byte[] _bytes = new byte[5];

    private void Set(int byteIndex, byte mask) => _bytes[byteIndex] |= mask;

    // Byte 1 — offline data authentication.
    /// <summary>Offline data authentication was not performed (this terminal is online-only).</summary>
    public void OfflineDataAuthNotPerformed() => Set(0, 0x80);

    // Byte 2 — processing restrictions.
    /// <summary>The application had expired (transaction date past tag 5F24).</summary>
    public void ExpiredApplication() => Set(1, 0x40);

    /// <summary>The application was not yet effective (transaction date before tag 5F25).</summary>
    public void ApplicationNotYetEffective() => Set(1, 0x20);

    /// <summary>The requested service is not allowed for this card product (AUC check failed).</summary>
    public void RequestedServiceNotAllowed() => Set(1, 0x10);

    // Byte 3 — cardholder verification.
    /// <summary>Cardholder verification was not successful.</summary>
    public void CardholderVerificationFailed() => Set(2, 0x80);

    /// <summary>Online PIN was entered.</summary>
    public void OnlinePinEntered() => Set(2, 0x04);

    // Byte 4 — terminal risk management.
    /// <summary>The transaction amount exceeded the terminal floor limit.</summary>
    public void ExceedsFloorLimit() => Set(3, 0x80);

    /// <summary>The transaction was selected randomly for online processing.</summary>
    public void SelectedRandomlyForOnline() => Set(3, 0x10);

    /// <summary>The five TVR bytes.</summary>
    public byte[] ToBytes() => (byte[])_bytes.Clone();
}
