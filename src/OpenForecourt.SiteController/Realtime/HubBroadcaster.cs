using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.SiteController.Contracts;
using OpenForecourt.SiteController.Faults;
using OpenForecourt.SiteController.Opt;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Realtime;

/// <summary>
/// Bridges domain change events to the SignalR feed: it forwards every pump-state change and
/// transaction lifecycle change to the <see cref="PerConnectionDispatcher"/>, which fans them
/// out to clients with per-connection backpressure.
/// </summary>
public sealed class HubBroadcaster(
    PerConnectionDispatcher dispatcher,
    PumpRegistry pumps,
    TransactionService transactions,
    OptService terminals,
    FaultInjector faults,
    SnapshotProvider snapshots,
    ILogger<HubBroadcaster> logger) : IHostedService
{
    // The statuses that move a total. Recomputing on every lifecycle write would re-read the
    // journal several times per sale for no visible change; these are the ones that count.
    private static readonly HashSet<TransactionStatus> TotalsAffecting =
    [
        TransactionStatus.Completed,
        TransactionStatus.Declined,
        TransactionStatus.Reversed,
        TransactionStatus.Approved, // an offline approval adds to the outstanding exposure
    ];


    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        pumps.PumpChanged += OnPumpChanged;
        transactions.TransactionUpdated += OnTransactionUpdated;
        terminals.OptChanged += OnOptChanged;
        faults.Changed += OnFaultsChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        pumps.PumpChanged -= OnPumpChanged;
        transactions.TransactionUpdated -= OnTransactionUpdated;
        terminals.OptChanged -= OnOptChanged;
        faults.Changed -= OnFaultsChanged;
        return Task.CompletedTask;
    }

    private void OnPumpChanged(object? sender, PumpSnapshot snapshot) =>
        dispatcher.Broadcast(new Frame("PumpUpdated", snapshot));

    private void OnTransactionUpdated(object? sender, TransactionContext tx)
    {
        dispatcher.Broadcast(new Frame("TransactionUpdated", TransactionView.From(tx)));
        if (TotalsAffecting.Contains(tx.Status))
        {
            _ = PublishTotalsAsync();
        }
    }

    // Fired from a synchronous domain event, so it cannot be awaited here. Every failure is
    // logged rather than swallowed: a totals bar that silently stops updating is worse than
    // one that visibly errors.
    private async Task PublishTotalsAsync()
    {
        try
        {
            var snapshot = await snapshots.BuildAsync(CancellationToken.None).ConfigureAwait(false);
            dispatcher.Broadcast(new Frame("TotalsUpdated", snapshot.Totals));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not broadcast updated site totals.");
        }
    }

    private void OnOptChanged(object? sender, OptView view) =>
        dispatcher.Broadcast(new Frame("OptUpdated", view));

    private void OnFaultsChanged(object? sender, FaultState state) =>
        dispatcher.Broadcast(new Frame("FaultsUpdated", state));
}
