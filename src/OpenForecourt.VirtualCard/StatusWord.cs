namespace OpenForecourt.VirtualCard;

/// <summary>
/// ISO 7816-4 status words (the two trailing bytes SW1 SW2 of every response APDU).
/// </summary>
/// <remarks>
/// Only the status words this card actually returns are named here. The values are fixed
/// by ISO 7816-4 and reused by EMV; the phase-2 spec calls out
/// <see cref="Success"/>, <see cref="FileNotFound"/>, <see cref="IncorrectP1P2"/>,
/// <see cref="WrongLength"/> and <see cref="ConditionsNotSatisfied"/> specifically.
/// </remarks>
public readonly record struct StatusWord(ushort Value)
{
    /// <summary>9000 — normal processing.</summary>
    public static StatusWord Success => new(0x9000);

    /// <summary>6A82 — file or application not found (e.g. SELECT of an unknown AID/PPSE).</summary>
    public static StatusWord FileNotFound => new(0x6A82);

    /// <summary>6A83 — record not found (READ RECORD past the end of a file).</summary>
    public static StatusWord RecordNotFound => new(0x6A83);

    /// <summary>6A86 — incorrect parameters P1-P2.</summary>
    public static StatusWord IncorrectP1P2 => new(0x6A86);

    /// <summary>6A88 — referenced data not found (GET DATA of an unknown tag).</summary>
    public static StatusWord ReferencedDataNotFound => new(0x6A88);

    /// <summary>6700 — wrong length.</summary>
    public static StatusWord WrongLength => new(0x6700);

    /// <summary>6985 — conditions of use not satisfied (e.g. GENERATE AC before GPO).</summary>
    public static StatusWord ConditionsNotSatisfied => new(0x6985);

    /// <summary>6D00 — instruction (INS) not supported.</summary>
    public static StatusWord InstructionNotSupported => new(0x6D00);

    /// <summary>The two status bytes, high byte first.</summary>
    public byte[] Bytes => [(byte)(Value >> 8), (byte)(Value & 0xFF)];

    /// <inheritdoc />
    public override string ToString() => Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
}
