using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Iso8583;

namespace OpenForecourt.SiteController.Tracing;

/// <summary>
/// Projects an ISO 8583 message to the field-by-field view the dashboard's detail drawer shows.
/// </summary>
/// <remarks>
/// Masking is driven by <see cref="FieldDefinition.Sensitive"/>, so it is a property of the
/// dialect table rather than of this code. A new sensitive field added to the dialect is masked
/// here the moment it is defined, with no change to the trace (CLAUDE.md section 7.3).
/// </remarks>
public static class Iso8583Trace
{
    /// <summary>A short label for the message: MTI plus its meaning.</summary>
    public static string Label(Iso8583Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return $"{message.Mti} {Iso8583Message.DescribeMti(message.Mti)}";
    }

    /// <summary>Describes the bitmap and every present field, masking the sensitive ones.</summary>
    public static IReadOnlyList<TraceField> Describe(Iso8583Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var fields = new List<TraceField>
        {
            new("MTI", $"{message.Mti} — {Iso8583Message.DescribeMti(message.Mti)}"),
            new("Bitmap", message.Bitmap.ToString()),
        };

        foreach (int number in message.PresentFields)
        {
            string value = message[number] ?? "";
            string name = message.Dialect.TryGet(number, out var definition) ? definition.Name : "(undefined)";
            bool sensitive = definition is { Sensitive: true };
            fields.Add(new TraceField($"DE {number:D3} {name}", sensitive ? Mask(value) : value));
        }

        return fields;
    }

    // A sensitive field is masked with the same first-6 + last-4 rule the log sink applies, so a
    // trace can never show more of a card number than a log can.
    private static string Mask(string value) =>
        value.Length >= 13 && value.All(char.IsAsciiDigit) ? new Pan(value).ToString() : new string('*', value.Length);
}
