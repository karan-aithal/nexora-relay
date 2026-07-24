using System.Collections.Frozen;

namespace OpenForecourt.Iso8583;

/// <summary>
/// A field table — the definition of one acquirer's ISO 8583 variant.
/// </summary>
/// <remarks>
/// Real acquirers each publish their own interface specification: which fields exist, how
/// lengths are encoded, whether numerics are ASCII or packed BCD. There is no single
/// "ISO 8583 on the wire". This project therefore defines and documents its own variant,
/// <see cref="Ofc87"/>, based on the ISO 8583:1987 field set — see
/// <c>docs/protocol-iso8583.md</c>. The codec holds no knowledge of any particular field;
/// point it at a different dialect and it speaks a different acquirer.
/// </remarks>
public sealed class Iso8583Dialect
{
    private readonly FrozenDictionary<int, FieldDefinition> _fields;

    /// <summary>Builds a dialect from a set of field definitions.</summary>
    /// <param name="name">Dialect name, used in diagnostics.</param>
    /// <param name="fields">The field definitions; field numbers must be unique and in 2..128.</param>
    public Iso8583Dialect(string name, IEnumerable<FieldDefinition> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Name = name;
        _fields = fields.ToFrozenDictionary(f => f.Number);

        foreach (int number in _fields.Keys)
        {
            if (number is < 2 or > 128)
            {
                throw new ArgumentOutOfRangeException(nameof(fields),
                    $"Field {number} is outside 2..128 (field 1 is the secondary-bitmap indicator, not a field).");
            }
        }
    }

    /// <summary>The dialect's name, e.g. <c>OFC-87</c>.</summary>
    public string Name { get; }

    /// <summary>The field numbers this dialect defines, ascending.</summary>
    public IEnumerable<int> DefinedFields => _fields.Keys.Order();

    /// <summary>Looks up a field definition.</summary>
    public bool TryGet(int number, out FieldDefinition definition) => _fields.TryGetValue(number, out definition!);

    /// <summary>Looks up a field definition, or throws if the dialect does not define it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The dialect has no such field.</exception>
    public FieldDefinition Get(int number) => TryGet(number, out var d)
        ? d
        : throw new ArgumentOutOfRangeException(nameof(number), $"Dialect {Name} does not define field {number}.");

    /// <summary>
    /// The OpenForecourt dialect: ISO 8583:1987 field set, ASCII everywhere except the
    /// bitmaps and field 55. Normative description in <c>docs/protocol-iso8583.md</c>.
    /// </summary>
    public static Iso8583Dialect Ofc87 { get; } = new("OFC-87",
    [
        new(2, "Primary account number", FieldType.LlVar, 19, FieldEncoding.Numeric, Sensitive: true),
        new(3, "Processing code", FieldType.Fixed, 6, FieldEncoding.Numeric),
        new(4, "Amount, transaction", FieldType.Fixed, 12, FieldEncoding.Numeric),
        new(7, "Transmission date and time", FieldType.Fixed, 10, FieldEncoding.Numeric),
        new(11, "System trace audit number", FieldType.Fixed, 6, FieldEncoding.Numeric),
        new(12, "Time, local transaction", FieldType.Fixed, 6, FieldEncoding.Numeric),
        new(13, "Date, local transaction", FieldType.Fixed, 4, FieldEncoding.Numeric),
        // SPEC-UNVERIFIED: 1987 defines field 22 as n 3; the 1993 revision widens it to n 12.
        new(22, "Point of service entry mode", FieldType.Fixed, 3, FieldEncoding.Numeric),
        new(37, "Retrieval reference number", FieldType.Fixed, 12, FieldEncoding.Alphanumeric),
        new(38, "Authorisation identification response", FieldType.Fixed, 6, FieldEncoding.Alphanumeric),
        new(39, "Response code", FieldType.Fixed, 2, FieldEncoding.Alphanumeric),
        new(41, "Card acceptor terminal identification", FieldType.Fixed, 8, FieldEncoding.Alphanumeric),
        new(42, "Card acceptor identification code", FieldType.Fixed, 15, FieldEncoding.Alphanumeric),
        // SPEC-UNVERIFIED: published as a 3 in some sources, n 3 in others. OFC-87 uses ISO 4217 numeric.
        new(49, "Currency code, transaction", FieldType.Fixed, 3, FieldEncoding.Numeric),
        // SPEC-UNVERIFIED: field 55 is reserved in 1987; EMV BER-TLV in field 55 is industry convention.
        new(55, "Integrated circuit card data", FieldType.LllVar, 999, FieldEncoding.Binary),
        new(70, "Network management information code", FieldType.Fixed, 3, FieldEncoding.Numeric),
        // SPEC-UNVERIFIED: MTI(4) + STAN(6) + transmission date-time(10) + acquirer id(11) + forwarder id(11).
        new(90, "Original data elements", FieldType.Fixed, 42, FieldEncoding.Numeric),
    ]);
}
