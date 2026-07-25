using OpenForecourt.PumpManager;
using Xunit;

namespace OpenForecourt.PumpManager.UnitTests;

/// <summary>
/// The C# codec tested against the SAME golden frames as the C tests
/// (<c>firmware/pump/test/test_frame.c</c>, <c>test_crc.c</c>). Cross-language agreement here is
/// the guard against the classic embedded integration bug — two codecs that each look correct
/// but disagree on the wire.
/// </summary>
public class OfpCodecTests
{
    [Fact]
    public void Crc_matches_the_pinned_check_value()
    {
        // docs §1.1: crc("123456789") == 0x29B1 — the value that makes "CRC16-CCITT" unambiguous.
        Assert.Equal(0x29B1, OfpCrc16.Compute("123456789"u8));
        Assert.Equal(0xFFFF, OfpCrc16.Compute([]));
        Assert.Equal(0xC0DC, OfpCrc16.Compute([0x07, 0x01, 0x02, 0x00, 0x00, 0x03, 0xE8]));
    }

    [Fact]
    public void Encodes_the_golden_authorise_frame_byte_for_byte()
    {
        // Same 15-byte stuffed frame the C golden test asserts (docs §9.1): SEQ 07, CMD 01,
        // payload 02 000003E8; body contains 0x02 and 0x03, both byte-stuffed to 10 22 / 10 23.
        var body = new OfpMessage(0x07, (byte)PumpCommand.Authorise, new byte[] { 0x02, 0x00, 0x00, 0x03, 0xE8 }).ToBody();
        byte[] wire = OfpWireCodec.EncodeWire(body);

        byte[] golden =
        [
            0x02, 0x00, 0x07, 0x07, 0x01, 0x10, 0x22, 0x00,
            0x00, 0x10, 0x23, 0xE8, 0xC0, 0xDC, 0x03,
        ];
        Assert.Equal(golden, wire);
    }

    [Fact]
    public void Round_trips_through_the_streaming_decoder()
    {
        var body = new OfpMessage(0x2A, (byte)PumpCommand.Authorise, new byte[] { 0x02, 0x00, 0x00, 0x03, 0xE8 }).ToBody();
        byte[] wire = OfpWireCodec.EncodeWire(body);

        var (status, message) = FeedAll(wire);
        Assert.Equal(OfpDecodeStatus.Ready, status);
        Assert.Equal(0x2A, message.Seq);
        Assert.Equal((byte)PumpCommand.Authorise, message.Code);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x03, 0xE8 }, message.Payload.ToArray());
    }

    [Fact]
    public void Stuffs_and_unstuffs_reserved_bytes_including_the_seq()
    {
        // SEQ 0x10 (ESC) and a payload of every reserved byte must all round-trip.
        var body = new OfpMessage(0x10, 0x22, new byte[] { 0x02, 0x03, 0x10, 0x20 }).ToBody();
        byte[] wire = OfpWireCodec.EncodeWire(body);

        // No raw STX/ETX between the delimiters.
        for (int i = 1; i < wire.Length - 1; i++)
        {
            Assert.NotEqual(OfpProtocol.Stx, wire[i]);
            Assert.NotEqual(OfpProtocol.Etx, wire[i]);
        }

        var (status, message) = FeedAll(wire);
        Assert.Equal(OfpDecodeStatus.Ready, status);
        Assert.Equal(0x10, message.Seq);
        Assert.Equal(new byte[] { 0x02, 0x03, 0x10, 0x20 }, message.Payload.ToArray());
    }

    [Fact]
    public void Rejects_a_corrupted_crc()
    {
        var body = new OfpMessage(0x40, 0x22, new byte[] { 0xAA, 0xBB, 0xCC }).ToBody();
        byte[] wire = OfpWireCodec.EncodeWire(body);
        wire[5] ^= 0xFF; // corrupt payload[0] on the wire (0xAA -> 0x55, still non-reserved)

        var decoder = new PumpFrameDecoder();
        OfpDecodeStatus status = OfpDecodeStatus.None;
        foreach (byte b in wire)
        {
            status = decoder.Feed(b);
        }

        Assert.Equal(OfpDecodeStatus.Error, status);
        Assert.Equal(PumpError.BadCrc, decoder.LastError);
    }

    [Fact]
    public void Resynchronises_after_a_truncated_frame()
    {
        var decoder = new PumpFrameDecoder();
        foreach (byte b in new byte[] { 0xAA, 0xBB, OfpProtocol.Stx, 0x99 }) // junk + doomed partial
        {
            decoder.Feed(b);
        }

        var body = new OfpMessage(0x55, (byte)PumpCommand.TotalsReq, ReadOnlyMemory<byte>.Empty).ToBody();
        OfpDecodeStatus status = OfpDecodeStatus.None;
        foreach (byte b in OfpWireCodec.EncodeWire(body))
        {
            status = decoder.Feed(b);
        }

        Assert.Equal(OfpDecodeStatus.Ready, status);
        Assert.Equal(0x55, decoder.Body.Span[0]);
    }

    private static (OfpDecodeStatus, OfpMessage) FeedAll(byte[] wire)
    {
        var decoder = new PumpFrameDecoder();
        OfpDecodeStatus status = OfpDecodeStatus.None;
        foreach (byte b in wire)
        {
            status = decoder.Feed(b);
        }

        return status == OfpDecodeStatus.Ready
            ? (status, OfpMessage.Parse(decoder.Body))
            : (status, default);
    }
}
