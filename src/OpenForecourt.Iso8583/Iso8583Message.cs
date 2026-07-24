using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using OpenForecourt.Abstractions.Domain;

namespace OpenForecourt.Iso8583;

/// <summary>
/// An ISO 8583 message: a 4-digit MTI plus a sparse set of numbered fields.
/// Immutable once built.
/// </summary>
/// <remarks>
/// <para>
/// Field values are held as <see cref="string"/>. Numeric and alphanumeric fields hold
/// their characters; <b>binary fields hold uppercase hex</b> (field 55 is
/// <c>"9F27018..."</c>, not raw bytes). One value type keeps the message, the traces and
/// the golden-file tests all trivially comparable, and the codec converts at the wire
/// boundary where the field table already says what the encoding is.
/// </para>
/// <para>
/// Build with <see cref="Create"/>, or with <see cref="ToBuilder"/> to derive a response
/// from a request. Values are validated against the dialect at build time: a value that
/// cannot be encoded is a programming error and throws, whereas anything that arrives from
/// the wire goes through <see cref="Iso8583Codec.TryDecode"/> and comes back as a
/// diagnostic error, never an exception.
/// </para>
/// </remarks>
public sealed class Iso8583Message
{
    private static readonly FrozenDictionary<string, string> MtiNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["0100"] = "Authorisation request",
        ["0110"] = "Authorisation response",
        ["0200"] = "Financial request",
        ["0210"] = "Financial response",
        ["0400"] = "Reversal request",
        ["0410"] = "Reversal response",
        ["0800"] = "Network management request",
        ["0810"] = "Network management response",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly FrozenDictionary<int, string> _fields;

    internal Iso8583Message(Iso8583Dialect dialect, string mti, IReadOnlyDictionary<int, string> fields)
    {
        Dialect = dialect;
        Mti = mti;
        _fields = fields.ToFrozenDictionary();
        Bitmap = Bitmap.ForFields(_fields.Keys);
    }

    /// <summary>The dialect this message is expressed in.</summary>
    public Iso8583Dialect Dialect { get; }

    /// <summary>The 4-digit message type indicator, e.g. <c>0200</c>.</summary>
    public string Mti { get; }

    /// <summary>The presence bitmap implied by the fields present.</summary>
    public Bitmap Bitmap { get; }

    /// <summary>The field numbers present, ascending.</summary>
    public IEnumerable<int> PresentFields => _fields.Keys.Order();

    /// <summary>The value of a field, or null when absent.</summary>
    public string? this[int field] => _fields.TryGetValue(field, out string? value) ? value : null;

    /// <summary>Gets a field value if present.</summary>
    public bool TryGetField(int field, out string value) => _fields.TryGetValue(field, out value!);

    /// <summary>Starts a new message.</summary>
    /// <param name="mti">The 4-digit MTI.</param>
    /// <param name="dialect">The dialect; defaults to <see cref="Iso8583Dialect.Ofc87"/>.</param>
    public static Iso8583MessageBuilder Create(string mti, Iso8583Dialect? dialect = null) =>
        new(dialect ?? Iso8583Dialect.Ofc87, mti);

    /// <summary>Starts a new message pre-loaded with this one's MTI and fields.</summary>
    public Iso8583MessageBuilder ToBuilder()
    {
        var builder = new Iso8583MessageBuilder(Dialect, Mti);
        foreach ((int field, string value) in _fields)
        {
            builder.Set(field, value);
        }

        return builder;
    }

    /// <summary>The human name of an MTI, or "Unknown message type".</summary>
    public static string DescribeMti(string mti) =>
        MtiNames.TryGetValue(mti, out string? name) ? name : "Unknown message type";

    /// <summary>
    /// A field-by-field trace, one line per field, with sensitive fields masked.
    /// This is the diagnostic you live in while building a payment system, so it renders
    /// the field number, name, wire descriptor and value, never a raw PAN.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"MTI {Mti}  {DescribeMti(Mti)}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"  bitmap {Bitmap}").AppendLine();

        foreach (int field in PresentFields)
        {
            string name = Dialect.TryGet(field, out var def) ? def.Name : "(not in dialect)";
            string descriptor = def is null ? string.Empty : def.Descriptor;
            sb.Append(CultureInfo.InvariantCulture,
                $"  {field,3} {name,-38} {descriptor,-14} {Render(field, def)}").AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// The trace rendering of one field's value. Sensitive fields never render in the clear:
    /// a numeric sensitive field (the PAN) is masked to first 6 + last 4 by
    /// <see cref="Pan"/>; anything else sensitive is redacted outright.
    /// </summary>
    private string Render(int field, FieldDefinition? definition)
    {
        string value = _fields[field];
        if (definition is null or { Sensitive: false })
        {
            return value;
        }

        return definition.Encoding == FieldEncoding.Numeric && value.Length > 0
            ? new Pan(value).ToString()
            : "[REDACTED]";
    }
}
