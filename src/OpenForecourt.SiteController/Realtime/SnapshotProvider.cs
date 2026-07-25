using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Contracts;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Realtime;

/// <summary>
/// Builds the authoritative site snapshot used to resynchronise a (re)connecting dashboard,
/// so a client that missed live frames while disconnected recovers exact state (CLAUDE.md
/// Phase 5, "Reconnection with state resynchronisation on the client").
/// </summary>
public sealed class SnapshotProvider(PumpRegistry pumps, ITransactionJournal journal)
{
    private const int RecentCount = 20;

    /// <summary>Builds the current snapshot: every pump plus the most recent transactions.</summary>
    public async Task<SiteSnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var all = await journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var recent = all.Take(RecentCount).Select(TransactionView.From).ToArray();
        return new SiteSnapshot(pumps.Snapshot(), recent);
    }
}
