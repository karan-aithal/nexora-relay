namespace OpenForecourt.PumpManager;

/// <summary>
/// CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, xorout 0) — the C# twin of
/// <c>firmware/pump/src/crc16.c</c>. Pinned by its published check value
/// <c>crc("123456789") == 0x29B1</c> (<c>docs §1.1</c>), asserted in the tests, so the phrase
/// "CRC16-CCITT" is unambiguous across the two implementations.
/// </summary>
public static class OfpCrc16
{
    /// <summary>Computes the CRC over <paramref name="data"/>.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }
}
