using System.Buffers;
using System.Globalization;
using System.Text;
using OpenForecourt.Abstractions;

namespace OpenForecourt.Iso8583;

/// <summary>Why a message could not be decoded, and where.</summary>
/// <param name="Offset">Byte offset into the message body at which the problem was found.</param>
/// <param name="Field">The field being decoded, or 0 for MTI/bitmap problems.</param>
/// <param name="Message">A human-readable diagnostic.</param>
public readonly record struct CodecError(int Offset, int Field, string Message)
{
    /// <inheritdoc />
    public override string ToString() => Field == 0
        ? $"offset {Offset}: {Message}"
        : $"offset {Offset}, field {Field}: {Message}";
}

/// <summary>
/// Encodes and decodes whole ISO 8583 messages against a dialect's field table.
/// </summary>
/// <remarks>
/// Decoding is the untrusted direction: every input comes off a socket, so
/// <see cref="TryDecode"/> returns a <see cref="Result{TValue,TError}"/> and never throws
/// on malformed input (CLAUDE.md section 6 — a bad message is an expected outcome, not an
/// exceptional one). Encoding is the trusted direction: the message was validated when it
/// was built, so it cannot fail.
/// </remarks>
public sealed class Iso8583Codec(Iso8583Dialect? dialect = null)
{
    private readonly Iso8583Dialect _dialect = dialect ?? Iso8583Dialect.Ofc87;

    /// <summary>The dialect this codec speaks.</summary>
    public Iso8583Dialect Dialect => _dialect;

    /// <summary>Encodes a message body: MTI, bitmap(s), then fields in ascending order.</summary>
    public byte[] Encode(Iso8583Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var writer = new ArrayBufferWriter<byte>(64);

        var mti = writer.GetSpan(4);
        Encoding.ASCII.GetBytes(message.Mti, mti);
        writer.Advance(4);

        var bitmap = message.Bitmap;
        int bitmapLength = bitmap.EncodedLength;
        bitmap.WriteTo(writer.GetSpan(bitmapLength));
        writer.Advance(bitmapLength);

        foreach (int field in message.PresentFields)
        {
            FieldCodec.Write(_dialect.Get(field), message[field]!, writer);
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Decodes a message body (no length prefix — see <see cref="Iso8583Framing"/>).
    /// </summary>
    public Result<Iso8583Message, CodecError> TryDecode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
        {
            return Fail(0, 0, $"truncated: need 4 MTI bytes, {body.Length} available.");
        }

        string mti = Encoding.ASCII.GetString(body[..4]);
        foreach (char c in mti)
        {
            if (!char.IsAsciiDigit(c))
            {
                return Fail(0, 0, "MTI must be 4 ASCII digits.");
            }
        }

        if (!Bitmap.TryRead(body[4..], out var bitmap, out int bitmapBytes))
        {
            return Fail(4, 0, "truncated: incomplete bitmap.");
        }

        int offset = 4 + bitmapBytes;
        var fields = new SortedDictionary<int, string>();

        foreach (int field in bitmap.PresentFields())
        {
            if (!_dialect.TryGet(field, out var definition))
            {
                return Fail(offset, field, $"dialect {_dialect.Name} does not define this field.");
            }

            if (!FieldCodec.TryRead(definition, body[offset..], out string value, out int consumed, out string? error))
            {
                return Fail(offset, field, error ?? "malformed field.");
            }

            fields[field] = value;
            offset += consumed;
        }

        if (offset != body.Length)
        {
            return Fail(offset, 0,
                string.Create(CultureInfo.InvariantCulture,
                    $"{body.Length - offset} trailing byte(s) after the last field described by the bitmap."));
        }

        return Result<Iso8583Message, CodecError>.Ok(new Iso8583Message(_dialect, mti, fields));
    }

    private static Result<Iso8583Message, CodecError> Fail(int offset, int field, string message) =>
        Result<Iso8583Message, CodecError>.Fail(new CodecError(offset, field, message));
}
