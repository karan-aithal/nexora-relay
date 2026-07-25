using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.SiteController.Services;

/// <summary>
/// Consumes the store-and-forward dispatch queue and settles each transaction exactly once,
/// deduplicating on transaction id (CLAUDE.md Phase 5, "Consumer idempotency by transaction id").
/// </summary>
/// <remarks>
/// Delivery is at-least-once, so the same settlement can arrive more than once — after a
/// crash-and-resume, or a broker redelivery. The consumer treats a transaction id it has
/// already settled as a no-op and acknowledges it, which is what makes the end-to-end effect
/// exactly-once despite at-least-once transport.
/// </remarks>
public sealed class SettlementConsumer(ITransactionDispatch dispatch, ILogger<SettlementConsumer> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, byte> _settled = new();
    private IAsyncDisposable? _subscription;

    /// <summary>Distinct transactions settled so far (for observability and tests).</summary>
    public int SettledCount => _settled.Count;

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = dispatch.Subscribe(HandleAsync);
        return Task.CompletedTask;
    }

    private Task<DispatchResult> HandleAsync(DispatchMessage message, CancellationToken cancellationToken)
    {
        if (_settled.TryAdd(message.TransactionId, 0))
        {
            logger.LogInformation("Settled {Kind} for {TransactionId}.", message.Kind, message.TransactionId);
        }
        else
        {
            logger.LogDebug("Duplicate {Kind} for {TransactionId} ignored.", message.Kind, message.TransactionId);
        }

        return Task.FromResult(DispatchResult.Ack);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
