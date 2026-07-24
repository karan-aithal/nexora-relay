using System.Collections.Concurrent;
using System.Globalization;

namespace OpenForecourt.HostSimulator;

/// <summary>One authorisation the simulated acquirer has seen.</summary>
/// <param name="TerminalId">Field 41.</param>
/// <param name="Stan">Field 11 — unique per terminal per day, which is why it is half the key.</param>
/// <param name="Mti">The MTI that created the entry.</param>
/// <param name="AmountMinor">Field 4, in minor units.</param>
/// <param name="CurrencyCode">Field 49.</param>
/// <param name="ResponseCode">Field 39 as answered.</param>
/// <param name="AuthCode">Field 38 when approved, else null.</param>
/// <param name="Reversed">True once a 0400 has reversed it.</param>
public sealed record LedgerEntry(
    string TerminalId,
    string Stan,
    string Mti,
    long AmountMinor,
    string CurrencyCode,
    string ResponseCode,
    string? AuthCode,
    bool Reversed = false);

/// <summary>
/// The simulated acquirer's in-memory book of transactions.
/// </summary>
/// <remarks>
/// <para>
/// It exists for two reasons. First, <b>duplicate detection</b>: a terminal that retries
/// after a timeout resends the same STAN, and a real host must not authorise twice — so the
/// key is terminal id + STAN, exactly as an acquirer keys it. Second, <b>reversal
/// verification</b>: a reversal that is not observable is not testable, so the demo can
/// print the outstanding total before and after and show the money actually came back.
/// </para>
/// <para>
/// Thread-safe: one instance is shared by every connection handler.
/// </para>
/// </remarks>
public sealed class HostLedger
{
    private readonly ConcurrentDictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);

    private static string Key(string terminalId, string stan) => $"{terminalId}|{stan}";

    /// <summary>
    /// Records the outcome of a request. Returns false when this terminal has already used
    /// this STAN — the caller must then answer <c>94 duplicate transmission</c>.
    /// </summary>
    public bool TryRecord(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _entries.TryAdd(Key(entry.TerminalId, entry.Stan), entry);
    }

    /// <summary>Finds an entry by its key.</summary>
    public bool TryGet(string terminalId, string stan, out LedgerEntry entry) =>
        _entries.TryGetValue(Key(terminalId, stan), out entry!);

    /// <summary>
    /// Reverses a previously approved transaction. Returns false when there is nothing to
    /// reverse (unknown original, a decline, or an already-reversed entry), which the caller
    /// answers as <c>21 no action taken</c>.
    /// </summary>
    public bool TryReverse(string terminalId, string stan, out long reversedMinor)
    {
        reversedMinor = 0;
        string key = Key(terminalId, stan);

        while (_entries.TryGetValue(key, out var existing))
        {
            if (existing.Reversed || existing.ResponseCode != HostResponseCodes.Approved)
            {
                return false;
            }

            // Compare-and-swap rather than a lock: two reversals racing for the same original
            // must not both succeed, or the ledger double-counts the refund.
            if (_entries.TryUpdate(key, existing with { Reversed = true }, existing))
            {
                reversedMinor = existing.AmountMinor;
                return true;
            }
        }

        return false;
    }

    /// <summary>Every entry, in no particular order.</summary>
    public IReadOnlyCollection<LedgerEntry> Entries => _entries.Values.ToArray();

    /// <summary>Total approved and not-yet-reversed amount, in minor units.</summary>
    public long OutstandingMinor => _entries.Values
        .Where(e => e is { ResponseCode: HostResponseCodes.Approved, Reversed: false })
        .Sum(e => e.AmountMinor);

    /// <summary>A one-line summary for demo output.</summary>
    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"{_entries.Count} transaction(s), outstanding {OutstandingMinor} minor units");
}
