using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.SiteController.Orchestration;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

public sealed class OfflineReplayTests
{
    // CLAUDE.md Phase 5: go offline, run 20 transactions, come back online, assert all replayed
    // exactly once.
    [Fact]
    public async Task Offline_transactions_replay_in_order_exactly_once()
    {
        var harness = new SiteHarness(new InMemoryJournal());

        // Host goes down: 20 authorisations under the floor limit are taken offline.
        harness.Host.Available = false;
        harness.Availability.MarkUnavailable();
        for (int pump = 1; pump <= 20; pump++)
        {
            var result = await harness.Service.AuthoriseAsync(harness.Request(pump % 8 + 1, 1000), default);
            Assert.Equal(AuthorisationDecision.ApprovedOffline, result.Decision);
        }

        Assert.Equal(20000, harness.Offline.CurrentExposure);
        Assert.Empty(harness.Host.Requests); // nothing reached the host while it was down

        // Host returns: replay every offline transaction, oldest first.
        harness.Host.Available = true;
        await DrainAsync(harness);

        // Every offline transaction reached the host exactly once and is now settled.
        Assert.Equal(20, harness.Host.Requests.Count);
        Assert.Equal(20, harness.Host.Requests.Select(r => r.TransactionId).Distinct().Count());
        var all = await harness.Journal.ReadAllAsync(default);
        Assert.Equal(20, all.Count(t => t.Status == TransactionStatus.Completed));
        Assert.Equal(0, harness.Offline.CurrentExposure);

        // A second drain is a no-op — the records are terminal, so nothing replays twice.
        await DrainAsync(harness);
        Assert.Equal(20, harness.Host.Requests.Count);
    }

    [Fact]
    public async Task Declined_replay_generates_a_reversal()
    {
        var harness = new SiteHarness(new InMemoryJournal());
        harness.Host.Available = false;
        harness.Availability.MarkUnavailable();
        var offline = await harness.Service.AuthoriseAsync(harness.Request(1, 1000), default);
        Assert.Equal(AuthorisationDecision.ApprovedOffline, offline.Decision);

        // Host returns but now rejects the (already-dispensed) offline authorisation.
        harness.Host.Available = true;
        harness.Host.Decide = _ => AuthorisationOutcome.Declined;
        await DrainAsync(harness);

        var tx = (await harness.Journal.ReadAllAsync(default))[0];
        Assert.Equal(TransactionStatus.Reversed, tx.Status); // exception/reversal record, not silently lost
        Assert.Equal(0, harness.Offline.CurrentExposure);
    }

    private static async Task DrainAsync(SiteHarness harness)
    {
        var incomplete = await harness.Journal.ReadIncompleteAsync(default);
        foreach (var tx in incomplete
            .Where(r => r.Offline && r.Status == TransactionStatus.Approved)
            .OrderBy(r => r.CreatedAt))
        {
            await harness.Service.ReplayOfflineAsync(tx, default);
        }
    }
}
