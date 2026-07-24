using System.Globalization;
using OpenForecourt.Iso8583;

namespace OpenForecourt.HostSimulator;

/// <summary>
/// Builds the terminal-side messages used by the demo and the end-to-end tests.
/// </summary>
/// <remarks>
/// This is not the production OPT message assembly — that arrives with the terminal itself
/// in a later phase. It exists so the host simulator can be exercised without one.
/// </remarks>
public static class TerminalMessages
{
    /// <summary>Terminal id used by the demo.</summary>
    public const string TerminalId = "OPT00001";

    /// <summary>Merchant id used by the demo.</summary>
    public const string MerchantId = "OPENFORECOURT01";

    /// <summary>ISO 4217 numeric code for GBP.</summary>
    public const string CurrencyGbp = "826";

    /// <summary>
    /// A <c>0200</c> financial request: goods and services purchase, chip entry, online PIN
    /// capable, with a token EMV field 55.
    /// </summary>
    /// <param name="pan">A test PAN. Never a real card number.</param>
    /// <param name="amountMinor">Amount in minor units.</param>
    /// <param name="stan">System trace audit number, unique per terminal.</param>
    /// <param name="now">Timestamp used for fields 7, 12 and 13.</param>
    /// <param name="iccDataHex">EMV BER-TLV for field 55, as hex.</param>
    public static Iso8583Message Financial(
        string pan,
        long amountMinor,
        string stan,
        DateTimeOffset now,
        string iccDataHex = "9F2701809F360200019F1002010A") =>
        Iso8583Message.Create("0200")
            .Set(Fields.Pan, pan)
            // 00 purchase, 00 from default account, 00 to default account.
            .Set(Fields.ProcessingCode, "000000")
            .Set(Fields.Amount, amountMinor)
            .Set(Fields.TransmissionDateTime, now.ToString("MMddHHmmss", CultureInfo.InvariantCulture))
            .Set(Fields.Stan, stan)
            .Set(Fields.LocalTime, now.ToString("HHmmss", CultureInfo.InvariantCulture))
            .Set(Fields.LocalDate, now.ToString("MMdd", CultureInfo.InvariantCulture))
            // 05 = chip read, 1 = terminal can accept a PIN.
            .Set(Fields.PosEntryMode, "051")
            .Set(Fields.TerminalId, TerminalId)
            .Set(Fields.MerchantId, MerchantId)
            .Set(Fields.Currency, CurrencyGbp)
            .Set(Fields.IccData, iccDataHex)
            .Build();

    /// <summary>
    /// A <c>0400</c> reversal of <paramref name="original"/>, carrying field 90 original data
    /// elements so the host can find what to reverse.
    /// </summary>
    /// <param name="original">The request being reversed.</param>
    /// <param name="stan">The reversal's own STAN.</param>
    /// <param name="now">Timestamp for field 7.</param>
    public static Iso8583Message Reversal(Iso8583Message original, string stan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(original);

        // Field 90, 42 digits: original MTI(4) | original STAN(6) | original transmission
        // date-time(10) | acquiring institution id(11) | forwarding institution id(11).
        // This project has no institution registry, so both identifiers are zero-filled.
        string originalDataElements = string.Concat(
            original.Mti,
            original[Fields.Stan],
            original[Fields.TransmissionDateTime],
            new string('0', 22));

        return Iso8583Message.Create("0400")
            .Set(Fields.ProcessingCode, original[Fields.ProcessingCode]!)
            .Set(Fields.Amount, original[Fields.Amount]!)
            .Set(Fields.TransmissionDateTime, now.ToString("MMddHHmmss", CultureInfo.InvariantCulture))
            .Set(Fields.Stan, stan)
            .Set(Fields.TerminalId, original[Fields.TerminalId]!)
            .Set(Fields.Currency, original[Fields.Currency]!)
            .Set(Fields.OriginalDataElements, originalDataElements)
            .Build();
    }

    /// <summary>A <c>0800</c> network management request: <c>301</c> echo test, <c>001</c> sign-on.</summary>
    public static Iso8583Message NetworkManagement(string code, string stan, DateTimeOffset now) =>
        Iso8583Message.Create("0800")
            .Set(Fields.TransmissionDateTime, now.ToString("MMddHHmmss", CultureInfo.InvariantCulture))
            .Set(Fields.Stan, stan)
            .Set(Fields.TerminalId, TerminalId)
            .Set(Fields.NetworkManagementCode, code)
            .Build();
}
