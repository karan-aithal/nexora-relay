using System.Globalization;

namespace OpenForecourt.Emv.BerTlv;

/// <summary>
/// A BER-TLV tag, packed big-endian into a <see cref="uint"/>.
/// </summary>
/// <remarks>
/// <para>
/// A BER tag is 1..N bytes. When the low 5 bits of the first byte are all set
/// (<c>0x1F</c>) the tag continues into subsequent bytes, each with bit 8 set to signal
/// "more follows" and the last with bit 8 clear. EMV tags are one or two bytes in
/// practice; this type supports up to four, which is more than any EMV tag needs.
/// </para>
/// <para>
/// A valid tag's first byte is non-zero, so packing the significant bytes into a
/// <see cref="uint"/> is lossless: <see cref="Bytes"/> reproduces the on-wire bytes
/// exactly, which is what the byte-identical round-trip guarantee depends on. Storing the
/// tag as a single integer also makes equality and dictionary lookup trivial.
/// </para>
/// </remarks>
public readonly record struct Tag(uint Value)
{
    /// <summary>Bit 6 (<c>0x20</c>) of the first tag byte: set for a constructed data object.</summary>
    public bool IsConstructed => (FirstByte & 0x20) != 0;

    private byte FirstByte
    {
        get
        {
            uint v = Value;
            while (v > 0xFF)
            {
                v >>= 8;
            }

            return (byte)v;
        }
    }

    /// <summary>The number of bytes in this tag's encoding (1..4).</summary>
    public int Length => Value switch
    {
        <= 0xFF => 1,
        <= 0xFFFF => 2,
        <= 0xFFFFFF => 3,
        _ => 4,
    };

    /// <summary>The tag's on-wire bytes, big-endian, with no leading zero byte.</summary>
    public byte[] Bytes
    {
        get
        {
            int len = Length;
            var bytes = new byte[len];
            uint v = Value;
            for (int i = len - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(v & 0xFF);
                v >>= 8;
            }

            return bytes;
        }
    }

    /// <summary>
    /// Reads one tag from the front of <paramref name="input"/>.
    /// </summary>
    /// <param name="input">Bytes starting at a tag.</param>
    /// <param name="tag">The parsed tag on success.</param>
    /// <param name="consumed">The number of bytes the tag occupied.</param>
    /// <returns>True on a well-formed tag; false if truncated or longer than four bytes.</returns>
    public static bool TryRead(ReadOnlySpan<byte> input, out Tag tag, out int consumed)
    {
        tag = default;
        consumed = 0;

        if (input.IsEmpty)
        {
            return false;
        }

        uint value = input[0];
        int i = 1;

        // Low 5 bits all set => multi-byte tag; continue while bit 8 of each byte is set.
        if ((input[0] & 0x1F) == 0x1F)
        {
            do
            {
                if (i >= input.Length || i >= 4)
                {
                    return false; // truncated, or tag longer than we support
                }

                value = (value << 8) | input[i];
            }
            while ((input[i++] & 0x80) != 0);
        }

        tag = new Tag(value);
        consumed = i;
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Convert.ToHexString(Bytes);

    /// <summary>Parses a tag from its hex string (e.g. <c>"9F26"</c>).</summary>
    public static Tag Parse(string hex) =>
        new(uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
