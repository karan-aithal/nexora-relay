using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace OpenForecourt.Iso8583;

/// <summary>What a read attempt produced.</summary>
public enum FrameStatus
{
    /// <summary>A complete message body was read.</summary>
    Frame,

    /// <summary>The peer closed the connection cleanly, between messages.</summary>
    EndOfStream,

    /// <summary>The stream violated the framing rules; the connection must be closed.</summary>
    ProtocolError,
}

/// <summary>The outcome of one <see cref="Iso8583FrameReader.ReadFrameAsync"/> call.</summary>
/// <param name="Status">Frame, end of stream, or protocol error.</param>
/// <param name="Body">The message body, without its length prefix. Empty unless <see cref="FrameStatus.Frame"/>.</param>
/// <param name="Error">The diagnostic when <see cref="FrameStatus.ProtocolError"/>; otherwise null.</param>
public readonly record struct FrameResult(FrameStatus Status, ReadOnlyMemory<byte> Body, string? Error);

/// <summary>Framing constants and the write side of the 2-byte big-endian length prefix.</summary>
public static class Iso8583Framing
{
    /// <summary>Default maximum accepted body length, in bytes.</summary>
    public const int DefaultMaxBodyLength = 4096;

    /// <summary>Bytes in the length prefix.</summary>
    public const int PrefixLength = 2;

    /// <summary>
    /// Writes a length-prefixed frame and flushes it.
    /// </summary>
    /// <param name="writer">The destination.</param>
    /// <param name="body">The message body; must be 1..65535 bytes.</param>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <exception cref="ArgumentOutOfRangeException">The body is empty or will not fit in a 16-bit prefix.</exception>
    public static ValueTask<FlushResult> WriteFrameAsync(
        PipeWriter writer,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (body.Length is 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(body),
                $"Frame body must be 1..{ushort.MaxValue} bytes, got {body.Length}.");
        }

        var span = writer.GetSpan(PrefixLength + body.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)body.Length);
        body.Span.CopyTo(span[PrefixLength..]);
        writer.Advance(PrefixLength + body.Length);

        return writer.FlushAsync(cancellationToken);
    }
}

/// <summary>
/// Reads length-prefixed ISO 8583 frames from a <see cref="PipeReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// TCP is a byte stream: a read can deliver half a message, or three messages at once, and
/// the split is chosen by the network, not by the sender. This reader is the only place
/// that knows that. It asks the pipe for whatever is available, tries to parse a frame, and
/// tells the pipe exactly how much it consumed versus how much it merely examined — which
/// is what makes the pipe wait for more data instead of spinning on the same short buffer.
/// </para>
/// <para>
/// A length prefix larger than <see cref="MaxBodyLength"/> is rejected before anything is
/// buffered, so a hostile or broken peer cannot make this process allocate an arbitrary
/// buffer by announcing a huge frame.
/// </para>
/// </remarks>
public sealed class Iso8583FrameReader(PipeReader reader, int maxBodyLength = Iso8583Framing.DefaultMaxBodyLength)
{
    private readonly PipeReader _reader = reader;

    /// <summary>The largest body this reader will accept, in bytes.</summary>
    public int MaxBodyLength { get; } = maxBodyLength > 0
        ? maxBodyLength
        : throw new ArgumentOutOfRangeException(nameof(maxBodyLength));

    /// <summary>Reads the next frame, waiting for more data as needed.</summary>
    public async ValueTask<FrameResult> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var read = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = read.Buffer;

            var outcome = TryParseFrame(ref buffer, out var body, out string? error);
            switch (outcome)
            {
                case ParseOutcome.Frame:
                    // Consumed through the end of the frame; everything after it stays buffered
                    // for the next call, which is what makes coalesced messages work.
                    _reader.AdvanceTo(buffer.Start, buffer.Start);
                    return new FrameResult(FrameStatus.Frame, body, null);

                case ParseOutcome.Error:
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    return new FrameResult(FrameStatus.ProtocolError, default, error);

                default:
                    if (read.IsCompleted)
                    {
                        // The peer stopped mid-frame (or mid-prefix). Clean close only counts
                        // when nothing partial is left over. Build the diagnostic before
                        // AdvanceTo — the buffer's memory may be recycled after that call.
                        var truncated = buffer.IsEmpty
                            ? new FrameResult(FrameStatus.EndOfStream, default, null)
                            : new FrameResult(FrameStatus.ProtocolError, default,
                                $"truncated frame: stream ended with {buffer.Length} unparsed byte(s).");
                        _reader.AdvanceTo(buffer.Start, buffer.End);
                        return truncated;
                    }

                    // Consumed nothing, examined everything: the next ReadAsync blocks until
                    // more bytes arrive rather than returning the same short buffer forever.
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    break;
            }
        }
    }

    private enum ParseOutcome
    {
        NeedMoreData,
        Frame,
        Error,
    }

    private ParseOutcome TryParseFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> body, out string? error)
    {
        body = default;
        error = null;

        if (buffer.Length < Iso8583Framing.PrefixLength)
        {
            return ParseOutcome.NeedMoreData;
        }

        Span<byte> prefix = stackalloc byte[Iso8583Framing.PrefixLength];
        buffer.Slice(0, Iso8583Framing.PrefixLength).CopyTo(prefix);
        int length = BinaryPrimitives.ReadUInt16BigEndian(prefix);

        if (length == 0)
        {
            error = "frame length prefix is zero.";
            return ParseOutcome.Error;
        }

        if (length > MaxBodyLength)
        {
            error = $"frame length {length} exceeds the maximum {MaxBodyLength}.";
            return ParseOutcome.Error;
        }

        if (buffer.Length < Iso8583Framing.PrefixLength + length)
        {
            return ParseOutcome.NeedMoreData;
        }

        body = buffer.Slice(Iso8583Framing.PrefixLength, length).ToArray();
        buffer = buffer.Slice(Iso8583Framing.PrefixLength + length);
        return ParseOutcome.Frame;
    }
}
