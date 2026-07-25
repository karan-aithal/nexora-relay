using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Services;

/// <summary>
/// Runs journal recovery once at startup: it scans for in-flight transactions and reconciles
/// each before the site accepts new work (CLAUDE.md Phase 5, "On startup, scan for in-flight
/// records and reconcile").
/// </summary>
/// <remarks>
/// Recovery runs to completion inside <see cref="StartAsync"/>, so by the time the host is
/// serving requests every crash-interrupted transaction has been voided, reversed, resumed or
/// re-queued for replay. This is the visible payoff of journalling intent before acting.
/// </remarks>
public sealed class RecoveryService(
    ITransactionJournal journal, TransactionService transactions, ILogger<RecoveryService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var incomplete = await journal.ReadIncompleteAsync(cancellationToken).ConfigureAwait(false);
        if (incomplete.Count == 0)
        {
            logger.LogInformation("Recovery: no in-flight transactions.");
            return;
        }

        logger.LogWarning("Recovery: {Count} in-flight transaction(s) to reconcile.", incomplete.Count);
        foreach (var tx in incomplete)
        {
            await transactions.ResumeAsync(tx, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Recovery: complete.");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
