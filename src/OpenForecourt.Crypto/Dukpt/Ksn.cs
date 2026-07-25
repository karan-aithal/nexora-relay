using System.Buffers.Binary;
using OpenForecourt.Abstractions;

namespace OpenForecourt.Crypto.Dukpt;

/// <summary>
/// A DUKPT Key Serial Number (ANSI X9.24-1): 10 bytes = a 59-bit Initial Key Serial
/// Number identifying the device, followed by a 21-bit transaction counter.
/// </summary>
/// <remarks>
/// <para>
/// The counter is the low 21 bits of the full 80-bit value. <see cref="Advance"/> bumps it
/// by one; at exhaustion (<see cref="MaxCounter"/>) it refuses to advance further, which is
/// the device behaviour the standard requires — a real terminal is retired rather than
/// wrapping its counter and reusing derived keys.
/// </para>
/// <para>
/// The 10 raw bytes are the on-wire value carried in ISO 8583 field 53/48; the host uses
/// them, together with the Base Derivation Key, to re-derive the exact same transaction key
/// the terminal used, with no shared secret ever transmitted.
/// </para>
/// </remarks>
public readonly record struct Ksn
{
    /// <summary>The largest 21-bit counter value; a device stops here.</summary>
    public const uint MaxCounter = 0x1FFFFF;

    private const ulong CounterMask = 0x1FFFFF;

    private readonly ulong _low64;   // rightmost 8 bytes of the KSN
    private readonly ushort _high16; // leftmost 2 bytes of the KSN

    /// <summary>Wraps 10 raw KSN bytes.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bytes"/> is not 10 bytes.</exception>
    public Ksn(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 10)
        {
            throw new ArgumentException("A KSN is exactly 10 bytes.", nameof(bytes));
        }

        _high16 = BinaryPrimitives.ReadUInt16BigEndian(bytes[..2]);
        _low64 = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
    }

    private Ksn(ushort high16, ulong low64)
    {
        _high16 = high16;
        _low64 = low64;
    }

    /// <summary>Parses a 20-hex-character KSN.</summary>
    public static Ksn Parse(string hex) => new(Convert.FromHexString(hex));

    /// <summary>The 21-bit transaction counter carried in the low bits.</summary>
    public uint Counter => (uint)(_low64 & CounterMask);

    /// <summary>The 10 raw bytes, big-endian.</summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), _high16);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(2), _low64);
        return buffer;
    }

    /// <summary>
    /// The rightmost 8 bytes with the 21-bit counter cleared. This is the register the
    /// DUKPT derivation ORs counter bits into; the leftmost 2 bytes never take part.
    /// </summary>
    internal ulong CounterlessLow64 => _low64 & ~CounterMask;

    /// <summary>
    /// Returns a copy with the counter advanced by one, or an error at exhaustion.
    /// </summary>
    public Result<Ksn, KsnError> Advance()
    {
        uint counter = Counter;
        if (counter >= MaxCounter)
        {
            return Result<Ksn, KsnError>.Fail(KsnError.CounterExhausted);
        }

        ulong low = (_low64 & ~CounterMask) | (counter + 1);
        return Result<Ksn, KsnError>.Ok(new Ksn(_high16, low));
    }

    /// <summary>Hex form (upper-case), suitable for a KSN field. Not sensitive.</summary>
    public override string ToString() => Convert.ToHexString(ToBytes());
}

/// <summary>Ways a KSN operation can fail.</summary>
public enum KsnError
{
    /// <summary>The 21-bit counter is at its maximum; the device must be retired.</summary>
    CounterExhausted,
}
