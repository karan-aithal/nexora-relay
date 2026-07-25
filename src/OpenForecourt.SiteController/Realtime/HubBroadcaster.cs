using Microsoft.Extensions.Hosting;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.SiteController.Contracts;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Realtime;

/// <summary>
/// Bridges domain change events to the SignalR feed: it forwards every pump-state change and
/// transaction lifecycle change to the <see cref="PerConnectionDispatcher"/>, which fans them
/// out to clients with per-connection backpressure.
/// </summary>
public sealed class HubBroadcaster(
    PerConnectionDispatcher dispatcher, PumpRegistry pumps, TransactionService transactions) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        pumps.PumpChanged += OnPumpChanged;
        transactions.TransactionUpdated += OnTransactionUpdated;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        pumps.PumpChanged -= OnPumpChanged;
        transactions.TransactionUpdated -= OnTransactionUpdated;
        return Task.CompletedTask;
    }

    private void OnPumpChanged(object? sender, PumpSnapshot snapshot) =>
        dispatcher.Broadcast(new Frame("PumpUpdated", snapshot));

    private void OnTransactionUpdated(object? sender, TransactionContext tx) =>
        dispatcher.Broadcast(new Frame("TransactionUpdated", TransactionView.From(tx)));
}
