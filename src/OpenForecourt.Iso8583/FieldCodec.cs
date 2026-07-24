using System.Buffers;
using System.Globalization;
using System.Text;

namespace OpenForecourt.Iso8583;

/// <summary>
/// Encodes and decodes a single field according to its <see cref="FieldDefinition"/>.
/// </summary>
/// <remarks>
/// Every field in the system goes through these three methods. There is no per-field
/// branching anywhere — the only inputs are the row from the field table and the value.
/// </remarks>
internal static class FieldCodec
{
    /// <summary>Number of wire units a value occupies: characters for text, bytes for binary.</summary>
    internal static int UnitLength(FieldDefinition definition, string value) =>
        definition.Encoding == FieldEncoding.Binary ? value.Length / 2 : value.Length;

    /// <summary>Returns null when the value is encodable per the definition, else a diagnostic.</summary>
    internal static string? Validate(FieldDefinition definition, string value)
    {
        switch (definition.Encoding)
        {
            case FieldEncoding.Numeric:
                foreach (char c in value)
                {
                    if (!char.IsAsciiDigit(c))
                    {
                        return $"value must be ASCII digits, found '{c}'.";
                    }
                }

                break;

            case FieldEncoding.Alphanumeric:
                foreach (char c in value)
                {
                    if (c is < ' ' or > '~')
                    {
                        return $"value must be printable ASCII, found character 0x{(int)c:X2}.";
                    }
                }

                break;

            case FieldEncoding.Binary:
                if (value.Length % 2 != 0)
                {
                    return "binary value must be an even number of hex digits.";
                }

                foreach (char c in value)
                {
                    if (!char.IsAsciiHexDigit(c))
                    {
                        return $"binary value must be hex, found '{c}'.";
                    }
                }

                break;

            default:
                return $"unsupported encoding {definition.Encoding}.";
        }

        int units = UnitLength(definition, value);
        if (units > definition.Length)
        {
            return $"length {units} exceeds the defined maximum {definition.Length}.";
        }

        if (definition.Type == FieldType.Fixed && definition.Encoding == FieldEncoding.Binary && units != definition.Length)
        {
            return $"fixed binary field must be exactly {definition.Length} bytes, got {units}.";
        }

        return null;
    }

    /// <summary>
    /// Pads a valid value to its canonical stored form: fixed numeric fields are
    /// right-justified and zero-filled, fixed alphanumeric fields left-justified and
    /// space-filled. Storing the canonical form means a message survives an encode/decode
    /// round trip unchanged, rather than acquiring padding on the way back.
    /// </summary>
    internal static string Canonicalize(FieldDefinition definition, string value) =>
        definition.Type == FieldType.Fixed
            ? definition.Encoding switch
            {
                FieldEncoding.Numeric => value.PadLeft(definition.Length, '0'),
                FieldEncoding.Alphanumeric => value.PadRight(definition.Length, ' '),
                _ => value,
            }
            : value;

    /// <summary>Writes the length prefix (if any) and the value.</summary>
    internal static void Write(FieldDefinition definition, string value, IBufferWriter<byte> writer)
    {
        int prefixDigits = definition.LengthPrefixDigits;
        if (prefixDigits > 0)
        {
            string prefix = UnitLength(definition, value).ToString(CultureInfo.InvariantCulture).PadLeft(prefixDigits, '0');
            WriteAscii(prefix, writer);
        }

        if (definition.Encoding == FieldEncoding.Binary)
        {
            byte[] bytes = Convert.FromHexString(value);
            writer.Write(bytes);
        }
        else
        {
            WriteAscii(value, writer);
        }
    }

    /// <summary>
    /// Reads one field from the front of <paramref name="source"/>.
    /// </summary>
    /// <param name="definition">The field's row from the dialect table.</param>
    /// <param name="source">Bytes starting at this field's length prefix or value.</param>
    /// <param name="value">The decoded value, in canonical stored form.</param>
    /// <param name="consumed">Bytes consumed, including any length prefix.</param>
    /// <param name="error">A diagnostic when decoding fails; null on success.</param>
    /// <returns>False when the field is malformed or truncated.</returns>
    internal static bool TryRead(
        FieldDefinition definition,
        ReadOnlySpan<byte> source,
        out string value,
        out int consumed,
        out string? error)
    {
        value = string.Empty;
        consumed = 0;

        int units = definition.Length;
        int prefixDigits = definition.LengthPrefixDigits;

        if (prefixDigits > 0)
        {
            if (source.Length < prefixDigits)
            {
                error = $"truncated: need {prefixDigits} length-prefix bytes, {source.Length} available.";
                return false;
            }

            Span<char> digits = stackalloc char[prefixDigits];
            for (int i = 0; i < prefixDigits; i++)
            {
                char c = (char)source[i];
                if (!char.IsAsciiDigit(c))
                {
                    error = $"length prefix is not ASCII digits (0x{source[i]:X2}).";
                    return false;
                }

                digits[i] = c;
            }

            units = int.Parse(digits, CultureInfo.InvariantCulture);
            if (units > definition.Length)
            {
                error = $"declared length {units} exceeds the defined maximum {definition.Length}.";
                return false;
            }

            consumed = prefixDigits;
            source = source[prefixDigits..];
        }

        // For text encodings one unit is one byte; for binary the prefix already counted bytes.
        if (source.Length < units)
        {
            error = $"truncated: need {units} value bytes, {source.Length} available.";
            return false;
        }

        var raw = source[..units];
        value = definition.Encoding == FieldEncoding.Binary
            ? Convert.ToHexString(raw)
            : Encoding.ASCII.GetString(raw);
        consumed += units;

        error = Validate(definition, value);
        return error is null;
    }

    private static void WriteAscii(string text, IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(text.Length);
        int written = Encoding.ASCII.GetBytes(text, span);
        writer.Advance(written);
    }
}
