using System.Buffers.Binary;

namespace OpenForecourt.Iso8583;

/// <summary>
/// The ISO 8583 presence bitmap: 128 bits saying which fields are in the message.
/// </summary>
/// <remarks>
/// <para>
/// Two 64-bit halves. Bit <i>n</i> (1-based) corresponds to field <i>n</i>, with bit 1 the
/// <b>most significant</b> bit of the first byte — the opposite of the usual
/// least-significant-bit-first convention, and the single most common source of bugs when
/// people write this by hand. Storing each half as a <see cref="ulong"/> makes that
/// explicit: bit <i>n</i> is <c>1UL &lt;&lt; (64 - n)</c>.
/// </para>
/// <para>
/// Bit 1 is not a field. It signals that a secondary bitmap (fields 65..128) follows the
/// primary one. It is derived, never set by the caller: it is on exactly when some field
/// above 64 is present.
/// </para>
/// </remarks>
public readonly struct Bitmap : IEquatable<Bitmap>
{
    /// <summary>Bytes in one bitmap half.</summary>
    public const int HalfLength = 8;

    private readonly ulong _primary;
    private readonly ulong _secondary;

    private Bitmap(ulong primary, ulong secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    /// <summary>True when any field above 64 is present, so a secondary bitmap is required.</summary>
    public bool HasSecondary => _secondary != 0;

    /// <summary>Total encoded size: 8 bytes, or 16 when a secondary bitmap is present.</summary>
    public int EncodedLength => HasSecondary ? HalfLength * 2 : HalfLength;

    /// <summary>Builds a bitmap from the field numbers present in a message.</summary>
    /// <param name="fields">Field numbers, each in 2..128.</param>
    /// <exception cref="ArgumentOutOfRangeException">A field number is outside 2..128.</exception>
    public static Bitmap ForFields(IEnumerable<int> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        ulong primary = 0, secondary = 0;
        foreach (int field in fields)
        {
            if (field is < 2 or > 128)
            {
                throw new ArgumentOutOfRangeException(nameof(fields),
                    $"Field {field} is outside 2..128; bit 1 is the secondary-bitmap indicator and is derived.");
            }

            if (field <= 64)
            {
                primary |= Mask(field);
            }
            else
            {
                secondary |= Mask(field - 64);
            }
        }

        // Bit 1 is derived: set exactly when a secondary bitmap will be emitted.
        if (secondary != 0)
        {
            primary |= Mask(1);
        }

        return new Bitmap(primary, secondary);
    }

    /// <summary>True when bit <paramref name="bit"/> (1..128) is set.</summary>
    public bool IsSet(int bit)
    {
        if (bit is < 1 or > 128)
        {
            return false;
        }

        return bit <= 64
            ? (_primary & Mask(bit)) != 0
            : (_secondary & Mask(bit - 64)) != 0;
    }

    /// <summary>The field numbers this bitmap marks present, ascending. Bit 1 is excluded — it is not a field.</summary>
    public IEnumerable<int> PresentFields()
    {
        for (int bit = 2; bit <= 128; bit++)
        {
            if (IsSet(bit))
            {
                yield return bit;
            }
        }
    }

    /// <summary>Writes the bitmap, big-endian, into <paramref name="destination"/>.</summary>
    /// <param name="destination">At least <see cref="EncodedLength"/> bytes.</param>
    /// <returns>The number of bytes written: 8 or 16.</returns>
    public int WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, _primary);
        if (!HasSecondary)
        {
            return HalfLength;
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[HalfLength..], _secondary);
        return HalfLength * 2;
    }

    /// <summary>
    /// Reads a bitmap from the start of <paramref name="source"/>, consuming 8 bytes, or 16
    /// when the primary bitmap's bit 1 says a secondary bitmap follows.
    /// </summary>
    /// <param name="source">The bytes at the bitmap position.</param>
    /// <param name="bitmap">The decoded bitmap.</param>
    /// <param name="bytesRead">8 or 16.</param>
    /// <returns>False when <paramref name="source"/> is too short.</returns>
    public static bool TryRead(ReadOnlySpan<byte> source, out Bitmap bitmap, out int bytesRead)
    {
        bitmap = default;
        bytesRead = 0;

        if (source.Length < HalfLength)
        {
            return false;
        }

        ulong primary = BinaryPrimitives.ReadUInt64BigEndian(source);
        ulong secondary = 0;
        bytesRead = HalfLength;

        if ((primary & Mask(1)) != 0)
        {
            if (source.Length < HalfLength * 2)
            {
                bytesRead = 0;
                return false;
            }

            secondary = BinaryPrimitives.ReadUInt64BigEndian(source[HalfLength..]);
            bytesRead = HalfLength * 2;
        }

        bitmap = new Bitmap(primary, secondary);
        return true;
    }

    /// <summary>Hex rendering of the bitmap, e.g. <c>7220000000808000</c>.</summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[HalfLength * 2];
        int written = WriteTo(bytes);
        return Convert.ToHexString(bytes[..written]);
    }

    /// <inheritdoc />
    public bool Equals(Bitmap other) => _primary == other._primary && _secondary == other._secondary;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Bitmap other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_primary, _secondary);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Bitmap left, Bitmap right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Bitmap left, Bitmap right) => !left.Equals(right);

    // Bit 1 is the most significant bit of the 64-bit half, so bit n is 1 << (64 - n).
    private static ulong Mask(int bitWithinHalf) => 1UL << (64 - bitWithinHalf);
}
