using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Services;

/// <summary>
/// Replays offline authorisations to the host, in the order they were taken, the moment the
/// host comes back (CLAUDE.md Phase 5, "On reconnection, replay in order").
/// </summary>
/// <remarks>
/// It wakes on the <see cref="HostAvailability.CameOnline"/> edge (and once at startup), then
/// drains every <see cref="TransactionStatus.Approved"/> offline record oldest-first. Each
/// record is driven terminal by <see cref="TransactionService.ReplayOfflineAsync"/>, and the
/// host dedupes on transaction id, so a record replays <b>exactly once</b> even across a crash
/// mid-drain.
/// </remarks>
public sealed class OfflineReplayService(
    ITransactionJournal journal,
    TransactionService transactions,
    HostAvailability availability,
    ILogger<OfflineReplayService> logger) : BackgroundService
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        availability.CameOnline += OnCameOnline;
        try
        {
            _wake.Writer.TryWrite(true); // attempt a drain at startup in case the host is already up
            await foreach (var _ in _wake.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (availability.IsAvailable)
                {
                    await DrainAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            availability.CameOnline -= OnCameOnline;
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var incomplete = await journal.ReadIncompleteAsync(cancellationToken).ConfigureAwait(false);
        var pending = incomplete
            .Where(r => r.Offline && r.Status == TransactionStatus.Approved)
            .OrderBy(r => r.CreatedAt)
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        logger.LogInformation("Replay: draining {Count} offline transaction(s).", pending.Length);
        foreach (var tx in pending)
        {
            if (!availability.IsAvailable)
            {
                break; // host dropped again; stop and wait for the next CameOnline
            }

            await transactions.ReplayOfflineAsync(tx, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnCameOnline(object? sender, EventArgs e) => _wake.Writer.TryWrite(true);
}
