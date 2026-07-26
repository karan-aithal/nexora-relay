using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Contracts;
using OpenForecourt.SiteController.Faults;
using OpenForecourt.SiteController.Opt;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Realtime;

/// <summary>
/// Builds the authoritative site snapshot used to resynchronise a (re)connecting dashboard,
/// so a client that missed live frames while disconnected recovers exact state (CLAUDE.md
/// Phase 5, "Reconnection with state resynchronisation on the client").
/// </summary>
/// <remarks>
/// The snapshot carries every piece of state the live feed can push — pumps, transactions,
/// terminals, armed faults and totals. That is what makes resynchronisation total: a client can
/// discard everything it holds, apply one snapshot and be exactly correct again, which is far
/// easier to reason about than replaying a gap it cannot see.
/// </remarks>
public sealed class SnapshotProvider(
    PumpRegistry pumps,
    ITransactionJournal journal,
    OptService terminals,
    FaultInjector faults,
    OfflinePolicy offline,
    SiteOptions options)
{
    private const int RecentCount = 20;

    /// <summary>Builds the current snapshot: pumps, recent transactions, terminals, faults and totals.</summary>
    public async Task<SiteSnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var all = await journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var recent = all.Take(RecentCount).Select(TransactionView.From).ToArray();
        return new SiteSnapshot(
            pumps.Snapshot(),
            recent,
            terminals.Snapshot(),
            faults.Snapshot(),
            TotalsView.From(all, offline.CurrentExposure, options.Currency));
    }
}
