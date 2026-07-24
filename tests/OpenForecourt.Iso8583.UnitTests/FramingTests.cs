using System.Buffers;
using System.IO.Pipelines;
using Xunit;

namespace OpenForecourt.Iso8583.UnitTests;

/// <summary>
/// TCP delivers bytes, not messages. These tests feed the frame reader the awkward
/// deliveries the network will eventually produce anyway: one byte at a time, three messages
/// in a single buffer, a frame that stops halfway, and a length prefix that lies.
/// </summary>
public class FramingTests
{
    private static readonly byte[] Body = "0200hello"u8.ToArray();

    private static byte[] Frame(ReadOnlySpan<byte> body)
    {
        byte[] framed = new byte[body.Length + 2];
        framed[0] = (byte)(body.Length >> 8);
        framed[1] = (byte)(body.Length & 0xFF);
        body.CopyTo(framed.AsSpan(2));
        return framed;
    }

    [Fact]
    public async Task Reads_a_frame_delivered_one_byte_at_a_time()
    {
        var pipe = new Pipe();
        var reader = new Iso8583FrameReader(pipe.Reader);

        var pending = reader.ReadFrameAsync(CancellationToken.None).AsTask();

        byte[] framed = Frame(Body);
        for (int i = 0; i < framed.Length; i++)
        {
            Assert.False(pending.IsCompleted, $"the reader produced a frame after only {i} byte(s).");
            await pipe.Writer.WriteAsync(framed.AsMemory(i, 1), CancellationToken.None);
        }

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(FrameStatus.Frame, result.Status);
        Assert.Equal(Body, result.Body.ToArray());
    }

    [Fact]
    public async Task Reads_three_frames_coalesced_into_one_buffer()
    {
        var pipe = new Pipe();
        var reader = new Iso8583FrameReader(pipe.Reader);

        byte[] first = "0800first"u8.ToArray();
        byte[] second = "0200second"u8.ToArray();
        byte[] third = "0400third"u8.ToArray();
        byte[] all = [.. Frame(first), .. Frame(second), .. Frame(third)];

        await pipe.Writer.WriteAsync(all, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        Assert.Equal(first, (await reader.ReadFrameAsync(CancellationToken.None)).Body.ToArray());
        Assert.Equal(second, (await reader.ReadFrameAsync(CancellationToken.None)).Body.ToArray());
        Assert.Equal(third, (await reader.ReadFrameAsync(CancellationToken.None)).Body.ToArray());
        Assert.Equal(FrameStatus.EndOfStream, (await reader.ReadFrameAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Reads_a_frame_split_across_the_prefix()
    {
        var pipe = new Pipe();
        var reader = new Iso8583FrameReader(pipe.Reader);
        byte[] framed = Frame(Body);

        // The nastiest split: the two length bytes arrive in different reads.
        await pipe.Writer.WriteAsync(framed.AsMemory(0, 1), CancellationToken.None);
        var pending = reader.ReadFrameAsync(CancellationToken.None).AsTask();
        await pipe.Writer.WriteAsync(framed.AsMemory(1), CancellationToken.None);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(FrameStatus.Frame, result.Status);
        Assert.Equal(Body, result.Body.ToArray());
    }

    [Fact]
    public async Task An_oversized_length_prefix_is_rejected_without_buffering_the_claim()
    {
        var pipe = new Pipe();
        var reader = new Iso8583FrameReader(pipe.Reader, maxBodyLength: 64);

        // Claims 65535 bytes, sends 4. A reader that trusted the prefix would allocate first.
        await pipe.Writer.WriteAsync(new byte[] { 0xFF, 0xFF, 1, 2, 3, 4 }, CancellationToken.None);

        var result = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.Equal(FrameStatus.ProtocolError, result.Status);
        Assert.Contains("exceeds the maximum", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_length_prefix_is_rejected()
    {
        var pipe = new Pipe();
        var reader = new Iso8583FrameReader(pipe.Reader);

        await pipe.Writer.WriteAsync(new byte[] { 0x00, 0x00 }, CancellationToken.None);

        var result = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.Equal(FrameStatus.ProtocolError, result.Status);
        Assert.Contains("zero", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_frame_is_a_protocol_error_not_a_clean_close()
    {
        var pipe = new Pipe();
        var reader = new Iso8583FrameReader(pipe.Reader);

        byte[] framed = Frame(Body);
        await pipe.Writer.WriteAsync(framed.AsMemory(0, framed.Length - 3), CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var result = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.Equal(FrameStatus.ProtocolError, result.Status);
        Assert.Contains("truncated", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_between_frames_is_a_clean_end_of_stream()
    {
        var pipe = new Pipe();
        var reader = new Iso8583FrameReader(pipe.Reader);

        await pipe.Writer.WriteAsync(Frame(Body), CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        Assert.Equal(FrameStatus.Frame, (await reader.ReadFrameAsync(CancellationToken.None)).Status);
        Assert.Equal(FrameStatus.EndOfStream, (await reader.ReadFrameAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Written_frames_carry_a_two_byte_big_endian_length()
    {
        var pipe = new Pipe();

        await Iso8583Framing.WriteFrameAsync(pipe.Writer, Body, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var read = await pipe.Reader.ReadAsync(CancellationToken.None);
        byte[] bytes = read.Buffer.ToArray();

        Assert.Equal(Frame(Body), bytes);
        Assert.Equal(0x00, bytes[0]);
        Assert.Equal(Body.Length, bytes[1]);
    }

    [Fact]
    public async Task An_empty_body_cannot_be_framed()
    {
        var pipe = new Pipe();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Iso8583Framing.WriteFrameAsync(pipe.Writer, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }
}
