namespace OpenForecourt.PumpManager;

/// <summary>
/// Runs up to eight pump sessions concurrently and shuts them all down cleanly. Each session
/// is an independent actor over its own transport; the service just owns their lifetime and
/// the single cancellation token that stops every one of them with no orphaned tasks — a
/// property asserted by a test (CLAUDE.md Phase 4).
/// </summary>
public sealed class PumpManagerService : IAsyncDisposable
{
    /// <summary>The maximum number of concurrent pumps this manager supports.</summary>
    public const int MaxPumps = 8;

    private readonly PumpSession[] _sessions;
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;

    /// <summary>Creates the service over a set of pre-built sessions (1..8).</summary>
    public PumpManagerService(IEnumerable<PumpSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions.ToArray();
        if (_sessions.Length is 0 or > MaxPumps)
        {
            throw new ArgumentOutOfRangeException(nameof(sessions),
                $"Expected 1..{MaxPumps} sessions, got {_sessions.Length}.");
        }
    }

    /// <summary>The managed sessions.</summary>
    public IReadOnlyList<PumpSession> Sessions => _sessions;

    /// <summary>Starts every session's loops. Idempotent.</summary>
    public void Start() => _run ??= Task.WhenAll(_sessions.Select(s => s.RunAsync(_cts.Token)));

    /// <summary>Cancels all sessions and waits for every loop to finish — no orphaned tasks.</summary>
    public async Task StopAsync()
    {
        if (_run is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(false); // RunAsync swallows cancellation, so this completes cleanly
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
