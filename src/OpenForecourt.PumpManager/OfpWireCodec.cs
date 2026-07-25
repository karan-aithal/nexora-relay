using System.Buffers.Binary;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.PumpManager;

/// <summary>
/// A parsed OFP-1 message: the logical frame body carried in an
/// <see cref="Abstractions.Ports.PumpFrame"/> payload, split into its sequence number,
/// command/event code and command-specific payload.
/// </summary>
/// <remarks>
/// A <see cref="PumpFrame.Payload"/> holds exactly <c>SEQ | CMD | PAYLOAD</c> — the span the
/// LEN field counts and the CRC covers (<c>docs §1</c>). The transport (TCP/serial) owns the
/// STX/LEN/CRC/ETX/stuffing wire framing via <see cref="OfpWireCodec"/>; the in-process
/// transport carries this body directly with no wire framing at all.
/// </remarks>
/// <param name="Seq">The sequence number (command SEQ, or event SEQ for unsolicited frames).</param>
/// <param name="Code">The command or event code.</param>
/// <param name="Payload">The code-specific payload; may be empty.</param>
public readonly record struct OfpMessage(byte Seq, byte Code, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Serialises to the logical body <c>SEQ | CMD | PAYLOAD</c>.</summary>
    public byte[] ToBody()
    {
        var body = new byte[2 + Payload.Length];
        body[0] = Seq;
        body[1] = Code;
        Payload.Span.CopyTo(body.AsSpan(2));
        return body;
    }

    /// <summary>Parses a logical body <c>SEQ | CMD | PAYLOAD</c>.</summary>
    /// <exception cref="ArgumentException">The body is shorter than the 2-byte header.</exception>
    public static OfpMessage Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length < 2)
        {
            throw new ArgumentException("OFP body must be at least SEQ+CMD (2 bytes).", nameof(body));
        }

        var span = body.Span;
        return new OfpMessage(span[0], span[1], body[2..]);
    }
}

/// <summary>
/// The OFP-1 wire codec — STX/LEN/CRC/ETX framing with DLE byte-stuffing (<c>docs §1</c>).
/// The exact C# twin of <c>firmware/pump/src/frame.c</c>; the golden-frame test asserts both
/// produce identical bytes. Used by the TCP and serial transports; the in-process transport
/// bypasses it.
/// </summary>
public static class OfpWireCodec
{
    /// <summary>
    /// Wraps a logical body (<c>SEQ | CMD | PAYLOAD</c>) into a complete wire frame: prepends
    /// LEN, appends the CRC over the body, then STX + byte-stuffed(LEN|body|CRC) + ETX.
    /// </summary>
    public static byte[] EncodeWire(ReadOnlySpan<byte> body)
    {
        // Un-stuffed frame body: LEN(2) | SEQ | CMD | PAYLOAD | CRC(2). LEN counts SEQ+CMD+PAYLOAD.
        Span<byte> full = stackalloc byte[body.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(full, (ushort)body.Length);
        body.CopyTo(full[2..]);
        ushort crc = OfpCrc16.Compute(body);
        BinaryPrimitives.WriteUInt16BigEndian(full[(2 + body.Length)..], crc);

        // STX + stuffed body + ETX. Worst case every body byte is stuffed (2x).
        var wire = new byte[(full.Length * 2) + 2];
        int pos = 0;
        wire[pos++] = OfpProtocol.Stx;
        foreach (byte b in full)
        {
            if (b is OfpProtocol.Stx or OfpProtocol.Etx or OfpProtocol.Esc)
            {
                wire[pos++] = OfpProtocol.Esc;
                wire[pos++] = (byte)(b ^ OfpProtocol.StuffXor);
            }
            else
            {
                wire[pos++] = b;
            }
        }

        wire[pos++] = OfpProtocol.Etx;
        return wire[..pos];
    }
}

/// <summary>The result of feeding one byte to <see cref="PumpFrameDecoder"/>.</summary>
public enum OfpDecodeStatus
{
    /// <summary>More bytes are needed.</summary>
    None,

    /// <summary>A valid frame completed; its body is in <see cref="PumpFrameDecoder.Body"/>.</summary>
    Ready,

    /// <summary>A corrupt frame was dropped; <see cref="PumpFrameDecoder.LastError"/> says why.</summary>
    Error,
}

/// <summary>
/// Streaming OFP-1 frame decoder — the C# twin of the decoder in
/// <c>firmware/pump/src/frame.c</c>. Fed one byte at a time from the wire; a raw STX always
/// starts a fresh frame, so it resynchronises after a truncated one.
/// </summary>
public sealed class PumpFrameDecoder
{
    private readonly byte[] _body = new byte[6 + OfpProtocol.MaxPayload];
    private int _bodyLen;
    private bool _inFrame;
    private bool _esc;
    private bool _overflow;

    /// <summary>The decoded logical body (<c>SEQ | CMD | PAYLOAD</c>), valid after <see cref="OfpDecodeStatus.Ready"/>.</summary>
    public ReadOnlyMemory<byte> Body { get; private set; }

    /// <summary>The error explaining the last <see cref="OfpDecodeStatus.Error"/>.</summary>
    public PumpError LastError { get; private set; }

    /// <summary>Discards any partial frame in progress (e.g. after a link drop).</summary>
    public void Reset()
    {
        _bodyLen = 0;
        _inFrame = false;
        _esc = false;
        _overflow = false;
    }

    /// <summary>Feeds one received byte.</summary>
    public OfpDecodeStatus Feed(byte b)
    {
        if (b == OfpProtocol.Stx)
        {
            _bodyLen = 0;
            _inFrame = true;
            _esc = false;
            _overflow = false;
            return OfpDecodeStatus.None;
        }

        if (!_inFrame)
        {
            return OfpDecodeStatus.None; // junk between frames
        }

        if (b == OfpProtocol.Etx)
        {
            return Finish();
        }

        byte value = b;
        if (_esc)
        {
            value = (byte)(b ^ OfpProtocol.StuffXor);
            _esc = false;
        }
        else if (b == OfpProtocol.Esc)
        {
            _esc = true;
            return OfpDecodeStatus.None;
        }

        if (_bodyLen >= _body.Length)
        {
            _overflow = true; // keep consuming until ETX, then report
            return OfpDecodeStatus.None;
        }

        _body[_bodyLen++] = value;
        return OfpDecodeStatus.None;
    }

    private OfpDecodeStatus Finish()
    {
        _inFrame = false;
        if (_overflow || _esc || _bodyLen < 6)
        {
            LastError = PumpError.BadFrame;
            return OfpDecodeStatus.Error;
        }

        int len = (_body[0] << 8) | _body[1]; // counts SEQ+CMD+PAYLOAD
        if (len < 2 || len + 4 != _bodyLen)
        {
            LastError = PumpError.BadFrame;
            return OfpDecodeStatus.Error;
        }

        ushort got = (ushort)((_body[2 + len] << 8) | _body[3 + len]);
        ushort want = OfpCrc16.Compute(_body.AsSpan(2, len));
        if (got != want)
        {
            LastError = PumpError.BadCrc;
            return OfpDecodeStatus.Error;
        }

        Body = _body.AsMemory(2, len);
        LastError = PumpError.None;
        return OfpDecodeStatus.Ready;
    }
}
