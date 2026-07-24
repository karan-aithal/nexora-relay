using System.Globalization;

namespace OpenForecourt.Iso8583;

/// <summary>
/// Builds an <see cref="Iso8583Message"/>. Values are validated against the dialect as they
/// are set, so an un-encodable message cannot be constructed.
/// </summary>
public sealed class Iso8583MessageBuilder
{
    private readonly Iso8583Dialect _dialect;
    private readonly SortedDictionary<int, string> _fields = [];
    private string _mti;

    internal Iso8583MessageBuilder(Iso8583Dialect dialect, string mti)
    {
        _dialect = dialect;
        _mti = ValidateMti(mti);
    }

    /// <summary>Changes the MTI, e.g. when deriving a <c>0210</c> response from a <c>0200</c>.</summary>
    public Iso8583MessageBuilder WithMti(string mti)
    {
        _mti = ValidateMti(mti);
        return this;
    }

    /// <summary>
    /// Sets a field. Binary fields take an uppercase hex string.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The dialect does not define the field.</exception>
    /// <exception cref="ArgumentException">The value cannot be encoded per the field definition.</exception>
    public Iso8583MessageBuilder Set(int field, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var definition = _dialect.Get(field);
        string? error = FieldCodec.Validate(definition, value);
        if (error is not null)
        {
            throw new ArgumentException($"Field {field} ({definition.Name}): {error}", nameof(value));
        }

        _fields[field] = FieldCodec.Canonicalize(definition, value);
        return this;
    }

    /// <summary>Sets a numeric field from an integer, zero-filled to the field's fixed length.</summary>
    public Iso8583MessageBuilder Set(int field, long value)
    {
        var definition = _dialect.Get(field);
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (definition is { Type: FieldType.Fixed, Encoding: FieldEncoding.Numeric })
        {
            text = text.PadLeft(definition.Length, '0');
        }

        return Set(field, text);
    }

    /// <summary>Removes a field if present.</summary>
    public Iso8583MessageBuilder Remove(int field)
    {
        _fields.Remove(field);
        return this;
    }

    /// <summary>Produces the immutable message.</summary>
    public Iso8583Message Build() => new(_dialect, _mti, _fields);

    private static string ValidateMti(string mti)
    {
        ArgumentNullException.ThrowIfNull(mti);
        if (mti.Length != 4 || !mti.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("MTI must be exactly 4 ASCII digits.", nameof(mti));
        }

        return mti;
    }
}
