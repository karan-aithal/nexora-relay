using OpenForecourt.Abstractions;

namespace OpenForecourt.Emv.BerTlv;

/// <summary>Why a BER-TLV byte string could not be parsed, and where.</summary>
/// <param name="Offset">Byte offset at which the problem was found.</param>
/// <param name="Message">A human-readable diagnostic.</param>
public readonly record struct TlvError(int Offset, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"offset {Offset}: {Message}";
}

/// <summary>
/// Parses and serialises BER-TLV byte strings. Parsing is the untrusted direction — input
/// arrives from a card over an APDU — so it returns a <see cref="Result{TValue,TError}"/>
/// and never throws or loops on malformed input (CLAUDE.md section 6, and the phase-2
/// requirement that truncated/malformed input returns errors).
/// </summary>
public static class BerTlvCodec
{
    /// <summary>
    /// Bounds recursion into constructed objects so an adversarial deeply-nested input
    /// cannot exhaust the stack. EMV structures nest no deeper than a handful of levels.
    /// </summary>
    private const int MaxDepth = 16;

    /// <summary>Parses a sequence of TLV objects from a byte string.</summary>
    public static Result<IReadOnlyList<TlvNode>, TlvError> TryParse(ReadOnlySpan<byte> input)
    {
        var nodes = new List<TlvNode>();
        var result = ParseSequence(input, 0, 0, nodes);
        return result.IsError
            ? Result<IReadOnlyList<TlvNode>, TlvError>.Fail(result.Error)
            : Result<IReadOnlyList<TlvNode>, TlvError>.Ok(nodes);
    }

    private static Result<bool, TlvError> ParseSequence(
        ReadOnlySpan<byte> input, int baseOffset, int depth, List<TlvNode> into)
    {
        if (depth > MaxDepth)
        {
            return Fail(baseOffset, "nesting deeper than supported.");
        }

        int pos = 0;
        while (pos < input.Length)
        {
            // Skip 0x00 / 0xFF padding bytes that may appear between top-level objects.
            if (input[pos] is 0x00 or 0xFF)
            {
                pos++;
                continue;
            }

            if (!Tag.TryRead(input[pos..], out var tag, out int tagLen))
            {
                return Fail(baseOffset + pos, "truncated or oversized tag.");
            }

            pos += tagLen;

            if (!TryReadLength(input[pos..], out int length, out int lenLen))
            {
                return Fail(baseOffset + pos, "truncated or malformed length.");
            }

            pos += lenLen;

            if (length > input.Length - pos)
            {
                return Fail(baseOffset + pos, $"value length {length} exceeds {input.Length - pos} remaining byte(s).");
            }

            var content = input.Slice(pos, length);

            if (tag.IsConstructed)
            {
                var children = new List<TlvNode>();
                var inner = ParseSequence(content, baseOffset + pos, depth + 1, children);
                if (inner.IsError)
                {
                    return inner;
                }

                into.Add(TlvNode.Constructed(tag, [.. children]));
            }
            else
            {
                into.Add(TlvNode.Primitive(tag, content));
            }

            pos += length;
        }

        return Result<bool, TlvError>.Ok(true);
    }

    /// <summary>Reads a BER definite-form length. Rejects the indefinite form (not valid in EMV).</summary>
    private static bool TryReadLength(ReadOnlySpan<byte> input, out int length, out int consumed)
    {
        length = 0;
        consumed = 0;

        if (input.IsEmpty)
        {
            return false;
        }

        byte first = input[0];
        if (first < 0x80)
        {
            length = first;
            consumed = 1;
            return true;
        }

        if (first == 0x80)
        {
            return false; // indefinite form — not permitted
        }

        int numBytes = first & 0x7F;
        if (numBytes > 4 || numBytes > input.Length - 1)
        {
            return false; // more length bytes than we support, or than are present
        }

        long value = 0;
        for (int i = 0; i < numBytes; i++)
        {
            value = (value << 8) | input[1 + i];
        }

        if (value > int.MaxValue)
        {
            return false;
        }

        length = (int)value;
        consumed = 1 + numBytes;
        return true;
    }

    private static Result<bool, TlvError> Fail(int offset, string message) =>
        Result<bool, TlvError>.Fail(new TlvError(offset, message));
}
