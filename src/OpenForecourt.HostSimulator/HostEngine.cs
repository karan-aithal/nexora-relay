using System.Globalization;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Iso8583;

namespace OpenForecourt.HostSimulator;

/// <summary>The response codes (field 39) this simulator issues. See <c>docs/protocol-iso8583.md</c>.</summary>
public static class HostResponseCodes
{
    /// <summary>Approved.</summary>
    public const string Approved = "00";

    /// <summary>Do not honour.</summary>
    public const string DoNotHonour = "05";

    /// <summary>Invalid transaction — used here for an unsupported MTI or missing mandatory field.</summary>
    public const string InvalidTransaction = "12";

    /// <summary>Invalid amount.</summary>
    public const string InvalidAmount = "13";

    /// <summary>No action taken — reversal of an original the host does not have.</summary>
    public const string NoActionTaken = "21";

    /// <summary>Insufficient funds.</summary>
    public const string InsufficientFunds = "51";

    /// <summary>Expired card.</summary>
    public const string ExpiredCard = "54";

    /// <summary>Duplicate transmission — same STAN from the same terminal.</summary>
    public const string DuplicateTransmission = "94";
}

/// <summary>
/// What the host decided to do about one request.
/// </summary>
/// <param name="Response">The message to send back, or null to stay silent (injected timeout).</param>
/// <param name="Latency">How long to wait before sending it.</param>
/// <param name="Note">A one-line explanation for the trace log.</param>
public sealed record HostDecision(Iso8583Message? Response, TimeSpan Latency, string Note);

/// <summary>
/// The acquirer's decision logic: request in, decision out. No sockets, no timers, no
/// shared state beyond the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Everything time-dependent is a <i>value</i> in the returned <see cref="HostDecision"/>
/// rather than a <c>Task.Delay</c> inside the engine. That is what makes the interesting
/// behaviour — "this PAN gets 3 seconds of latency", "this PAN gets no answer at all" —
/// unit-testable in microseconds; only <see cref="HostServer"/> actually sleeps.
/// </para>
/// <para>
/// Thread-safe: the engine holds no mutable state of its own and the ledger is concurrent.
/// </para>
/// </remarks>
public sealed class HostEngine(HostRules rules, HostLedger ledger, IClock clock)
{
    private int _authCodeSequence;

    /// <summary>The rules in force.</summary>
    public HostRules Rules { get; } = rules;

    /// <summary>The book of transactions this engine maintains.</summary>
    public HostLedger Ledger { get; } = ledger;

    /// <summary>Decides what to do with a request message.</summary>
    public HostDecision Handle(Iso8583Message request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Mti switch
        {
            "0100" or "0200" => Authorise(request),
            "0400" => Reverse(request),
            "0800" => NetworkManagement(request),
            _ => new HostDecision(
                Respond(request, HostResponseCodes.InvalidTransaction, null),
                Latency(request),
                $"unsupported MTI {request.Mti}"),
        };
    }

    private HostDecision Authorise(Iso8583Message request)
    {
        string pan = request[Fields.Pan] ?? string.Empty;
        string terminal = request[Fields.TerminalId] ?? string.Empty;
        string stan = request[Fields.Stan] ?? string.Empty;
        var latency = Latency(request);

        if (Rules.NoResponsePans.Contains(pan, StringComparer.Ordinal))
        {
            // Deliberate silence. The terminal must time out, then reverse.
            return new HostDecision(null, latency, "no response injected for this PAN");
        }

        if (terminal.Length == 0 || stan.Length == 0)
        {
            return new HostDecision(
                Respond(request, HostResponseCodes.InvalidTransaction, null),
                latency,
                "missing terminal id (41) or STAN (11)");
        }

        if (!long.TryParse(request[Fields.Amount], NumberStyles.None, CultureInfo.InvariantCulture, out long amount)
            || amount <= 0)
        {
            return new HostDecision(
                Respond(request, HostResponseCodes.InvalidAmount, null),
                latency,
                "amount (4) missing, non-numeric or not positive");
        }

        string responseCode = Rules.DeclineByPan.TryGetValue(pan, out string? forced)
            ? forced
            : amount < Rules.ApproveBelowMinor
                ? HostResponseCodes.Approved
                : HostResponseCodes.InsufficientFunds;

        string? authCode = responseCode == HostResponseCodes.Approved ? NextAuthCode() : null;

        var entry = new LedgerEntry(
            terminal,
            stan,
            request.Mti,
            amount,
            request[Fields.Currency] ?? "000",
            responseCode,
            authCode);

        if (!Ledger.TryRecord(entry))
        {
            // Same terminal, same STAN: this is a retry of something already answered.
            return new HostDecision(
                Respond(request, HostResponseCodes.DuplicateTransmission, null),
                latency,
                $"duplicate STAN {stan} from terminal {terminal}");
        }

        string note = responseCode == HostResponseCodes.Approved
            ? $"approved {amount} minor units, auth code {authCode}"
            : $"declined {responseCode}";

        return new HostDecision(Respond(request, responseCode, authCode), latency, note);
    }

    private HostDecision Reverse(Iso8583Message request)
    {
        var latency = Latency(request);
        string terminal = request[Fields.TerminalId] ?? string.Empty;
        string original = request[Fields.OriginalDataElements] ?? string.Empty;

        // Field 90 is fixed 42 digits: MTI(4) STAN(6) transmission date-time(10)
        // acquirer id(11) forwarder id(11). The STAN is what identifies the original.
        if (original.Length != 42 || terminal.Length == 0)
        {
            return new HostDecision(
                Respond(request, HostResponseCodes.InvalidTransaction, null),
                latency,
                "reversal missing or malformed original data elements (90)");
        }

        string originalStan = original.Substring(4, 6);

        if (!Ledger.TryReverse(terminal, originalStan, out long reversed))
        {
            return new HostDecision(
                Respond(request, HostResponseCodes.NoActionTaken, null),
                latency,
                $"nothing to reverse for terminal {terminal} STAN {originalStan}");
        }

        return new HostDecision(
            Respond(request, HostResponseCodes.Approved, null),
            latency,
            $"reversed {reversed} minor units for STAN {originalStan}");
    }

    private HostDecision NetworkManagement(Iso8583Message request)
    {
        string code = request[Fields.NetworkManagementCode] ?? string.Empty;
        bool known = code is "001" or "002" or "301";

        var response = request.ToBuilder()
            .WithMti(ResponseMti(request.Mti))
            .Set(Fields.ResponseCode, known ? HostResponseCodes.Approved : HostResponseCodes.InvalidTransaction)
            .Set(Fields.TransmissionDateTime, clock.UtcNow.ToString("MMddHHmmss", CultureInfo.InvariantCulture))
            .Remove(Fields.Pan)
            .Build();

        string meaning = code switch
        {
            "001" => "sign-on",
            "002" => "sign-off",
            "301" => "echo test",
            _ => $"unknown network management code '{code}'",
        };

        return new HostDecision(response, Latency(request), meaning);
    }

    /// <summary>
    /// Builds the response from the request: same fields, response MTI, plus field 39 and
    /// (when approved) field 38. Echoing the request's fields back is what lets a terminal
    /// match a response to the transaction it belongs to.
    /// </summary>
    private Iso8583Message Respond(Iso8583Message request, string responseCode, string? authCode)
    {
        var builder = request.ToBuilder()
            .WithMti(ResponseMti(request.Mti))
            .Set(Fields.ResponseCode, responseCode)
            .Set(Fields.TransmissionDateTime, clock.UtcNow.ToString("MMddHHmmss", CultureInfo.InvariantCulture))
            // The PAN does not travel back. Nothing downstream needs it, and every hop that
            // does not carry it is a hop out of PCI scope (CLAUDE.md section 7).
            .Remove(Fields.Pan)
            .Remove(Fields.IccData);

        if (authCode is not null)
        {
            builder.Set(Fields.AuthorisationCode, authCode);
            builder.Set(Fields.RetrievalReferenceNumber, RetrievalReference(request));
        }

        return builder.Build();
    }

    /// <summary>Response MTI is the request MTI plus 10: 0200 becomes 0210, 0400 becomes 0410.</summary>
    /// <summary>Response MTI is the request MTI plus 10.</summary>
    public static string ResponseMti(string requestMti) =>
        (int.Parse(requestMti, CultureInfo.InvariantCulture) + 10).ToString("0000", CultureInfo.InvariantCulture);

    private TimeSpan Latency(Iso8583Message request)
    {
        int ms = Rules.LatencyMs;
        if (request[Fields.Pan] is { } pan && Rules.ExtraLatencyByPanMs.TryGetValue(pan, out int extra))
        {
            ms += extra;
        }

        return TimeSpan.FromMilliseconds(ms);
    }

    private string NextAuthCode() =>
        "A" + (Interlocked.Increment(ref _authCodeSequence) % 100000).ToString("00000", CultureInfo.InvariantCulture);

    private string RetrievalReference(Iso8583Message request) =>
        clock.UtcNow.ToString("yyMMdd", CultureInfo.InvariantCulture) + (request[Fields.Stan] ?? "000000");
}
