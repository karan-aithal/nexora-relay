namespace OpenForecourt.VirtualCard;

/// <summary>
/// A parsed ISO 7816-4 command APDU: header (CLA INS P1 P2) plus optional command data.
/// </summary>
/// <remarks>
/// Only the short-form cases the card needs are parsed: header-only (case 1), header + Le
/// (case 2), header + Lc + data (case 3), and header + Lc + data + Le (case 4). Extended
/// length is not used by any command in this project. Parsing never throws; a malformed
/// APDU yields <see cref="TryParse"/> = false and the card answers <c>6700</c>.
/// </remarks>
public readonly record struct CommandApdu(byte Cla, byte Ins, byte P1, byte P2, ReadOnlyMemory<byte> Data)
{
    /// <summary>Parses a command APDU. Returns false on a truncated or inconsistent header.</summary>
    public static bool TryParse(ReadOnlySpan<byte> apdu, out CommandApdu command)
    {
        command = default;
        if (apdu.Length < 4)
        {
            return false;
        }

        byte cla = apdu[0], ins = apdu[1], p1 = apdu[2], p2 = apdu[3];

        // Case 1: header only.
        if (apdu.Length == 4)
        {
            command = new CommandApdu(cla, ins, p1, p2, ReadOnlyMemory<byte>.Empty);
            return true;
        }

        // Case 2: header + single Le byte.
        if (apdu.Length == 5)
        {
            command = new CommandApdu(cla, ins, p1, p2, ReadOnlyMemory<byte>.Empty);
            return true;
        }

        // Cases 3 & 4: header + Lc + data [+ Le].
        int lc = apdu[4];
        if (lc == 0)
        {
            return false; // Lc of 0 with trailing bytes is extended-length, which we do not use.
        }

        int dataStart = 5;
        if (apdu.Length < dataStart + lc)
        {
            return false; // declared Lc runs past the buffer
        }

        // Either exactly the data (case 3) or the data plus one Le byte (case 4).
        int remaining = apdu.Length - (dataStart + lc);
        if (remaining is not (0 or 1))
        {
            return false;
        }

        command = new CommandApdu(cla, ins, p1, p2, apdu.Slice(dataStart, lc).ToArray());
        return true;
    }
}
