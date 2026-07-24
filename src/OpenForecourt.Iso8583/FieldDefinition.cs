namespace OpenForecourt.Iso8583;

/// <summary>How a field's length is expressed on the wire.</summary>
public enum FieldType
{
    /// <summary>Always exactly <see cref="FieldDefinition.Length"/> units; no length prefix.</summary>
    Fixed,

    /// <summary>Variable length up to 99 units, prefixed by 2 ASCII digits.</summary>
    LlVar,

    /// <summary>Variable length up to 999 units, prefixed by 3 ASCII digits.</summary>
    LllVar,
}

/// <summary>How a field's value is represented on the wire.</summary>
public enum FieldEncoding
{
    /// <summary>ASCII digits only. Fixed fields are right-justified and zero-filled.</summary>
    Numeric,

    /// <summary>Printable ASCII. Fixed fields are left-justified and space-filled.</summary>
    Alphanumeric,

    /// <summary>Raw bytes. Held in <see cref="Iso8583Message"/> as an uppercase hex string.</summary>
    Binary,
}

/// <summary>
/// One row of the OFC-87 field table: everything the codec needs to encode or decode a
/// field, and nothing about what the field <i>means</i>.
/// </summary>
/// <remarks>
/// This is the whole point of a data-driven codec. There is no per-field parsing code
/// anywhere; adding a field is adding a row, and supporting a different acquirer's dialect
/// is supplying a different table. See <c>docs/protocol-iso8583.md</c>.
/// </remarks>
/// <param name="Number">The ISO 8583 field number (2..128).</param>
/// <param name="Name">Human-readable name, used in traces.</param>
/// <param name="Type">Fixed, LLVAR or LLLVAR.</param>
/// <param name="Length">Fixed length, or maximum length for variable fields, in units of <paramref name="Encoding"/>.</param>
/// <param name="Encoding">Numeric, alphanumeric or binary.</param>
/// <param name="Sensitive">
/// When true the value is never rendered in a trace. Field 2 (PAN) is masked; anything
/// added later that must never appear at all (PIN block) is redacted entirely.
/// </param>
public sealed record FieldDefinition(
    int Number,
    string Name,
    FieldType Type,
    int Length,
    FieldEncoding Encoding,
    bool Sensitive = false)
{
    /// <summary>Number of ASCII digits in this field's length prefix; 0 for fixed fields.</summary>
    public int LengthPrefixDigits => Type switch
    {
        FieldType.LlVar => 2,
        FieldType.LllVar => 3,
        _ => 0,
    };

    /// <summary>Short form used in traces, e.g. <c>LLVAR n..19</c> or <c>n 6</c>.</summary>
    public string Descriptor => Type switch
    {
        FieldType.Fixed => $"{EncodingCode} {Length}",
        FieldType.LlVar => $"LLVAR {EncodingCode}..{Length}",
        FieldType.LllVar => $"LLLVAR {EncodingCode}..{Length}",
        _ => EncodingCode,
    };

    private string EncodingCode => Encoding switch
    {
        FieldEncoding.Numeric => "n",
        FieldEncoding.Alphanumeric => "ans",
        _ => "b",
    };
}
