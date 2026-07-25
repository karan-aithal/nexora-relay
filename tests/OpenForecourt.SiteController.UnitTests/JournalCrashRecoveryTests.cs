using Microsoft.Extensions.Logging.Abstractions;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.Adapters.Sqlite;
using OpenForecourt.SiteController.Orchestration;
using OpenForecourt.SiteController.Services;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

/// <summary>
/// CLAUDE.md Phase 5: kill (here, simulate) the process at each of five defined points in the
/// transaction lifecycle and assert correct recovery on restart. "Restart" is modelled by
/// closing the SQLite journal and reopening it on the same file, exactly as a crashed process
/// would find it.
/// </summary>
public sealed class JournalCrashRecoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ofc-recovery-{Guid.NewGuid():N}.db");

    // POINT 1 — intent written, host never contacted -> void it.
    [Fact]
    public Task Intent_only_is_voided() =>
        RecoverAndAssert(TransactionStatus.Intent, offline: false, expected: TransactionStatus.Voided);

    // POINT 2 — request sent, no response recorded: host state unknown -> reverse.
    [Fact]
    public Task Host_request_sent_is_reversed() =>
        RecoverAndAssert(TransactionStatus.HostRequestSent, offline: false, expected: TransactionStatus.Reversed);

    // POINT 3 — online approval that never settled -> resume settlement.
    [Fact]
    public Task Online_approval_resumes_settlement() =>
        RecoverAndAssert(TransactionStatus.Approved, offline: false, expected: TransactionStatus.Completed);

    // POINT 4 — offline approval awaiting replay -> stays queued, exposure re-tracked.
    [Fact]
    public async Task Offline_approval_stays_queued_and_re_tracks_exposure()
    {
        var (harness, tx) = await RecoverAsync(TransactionStatus.Approved, offline: true);
        Assert.Equal(TransactionStatus.Approved, tx.Status);
        Assert.True(tx.Offline);
        Assert.Equal(2500, harness.Offline.CurrentExposure);
    }

    // POINT 5 — already completed -> recovery is a no-op.
    [Fact]
    public Task Completed_is_left_untouched() =>
        RecoverAndAssert(TransactionStatus.Completed, offline: false, expected: TransactionStatus.Completed);

    // The five points are real: a normal approval journals Intent -> HostRequestSent -> Approved
    // -> Completed, in that order, each before its side effect.
    [Fact]
    public async Task Authorise_journals_each_lifecycle_step_in_order()
    {
        var recording = new RecordingJournal(new InMemoryJournal());
        var harness = new SiteHarness(recording);
        await harness.Service.AuthoriseAsync(harness.Request(1, 2500), default);

        Assert.Equal(
            new[]
            {
                TransactionStatus.Intent,
                TransactionStatus.HostRequestSent,
                TransactionStatus.Approved,
                TransactionStatus.Completed,
            },
            recording.Statuses);
    }

    private async Task RecoverAndAssert(TransactionStatus seed, bool offline, TransactionStatus expected)
    {
        var (_, tx) = await RecoverAsync(seed, offline);
        Assert.Equal(expected, tx.Status);
    }

    private async Task<(SiteHarness Harness, TransactionContext Recovered)> RecoverAsync(TransactionStatus seed, bool offline)
    {
        var id = Guid.CreateVersion7();

        // Write the record the crashed process would have left, then "die" (close the journal).
        var writer = await SqliteJournal.OpenAsync(_path, default);
        await writer.AppendAsync(Seed(id, seed, offline), default);
        await writer.DisposeAsync();

        // Restart: reopen the same file and run recovery.
        var journal = await SqliteJournal.OpenAsync(_path, default);
        var harness = new SiteHarness(journal);
        var recovery = new RecoveryService(journal, harness.Service, NullLogger<RecoveryService>.Instance);
        await recovery.StartAsync(default);

        var recovered = await journal.TryGetAsync(id, default);
        Assert.NotNull(recovered);
        await journal.DisposeAsync();
        return (harness, recovered!);
    }

    private static TransactionContext Seed(Guid id, TransactionStatus status, bool offline)
    {
        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var state = status == TransactionStatus.Approved ? PumpState.Authorised : PumpState.Authorising;
        return new TransactionContext(
            id, PumpId: 1, State: state, Status: status,
            Amount: new Money(2500, "GBP"), Token: "TOK-1",
            Stan: status >= TransactionStatus.HostRequestSent ? "000001" : null,
            AuthorisationCode: status == TransactionStatus.Approved ? (offline ? "OFFLINE" : "A12345") : null,
            Offline: offline, CreatedAt: now, UpdatedAt: now, CorrelationId: Guid.NewGuid());
    }

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>Wraps a journal and records the status of every appended record, to assert write ordering.</summary>
internal sealed class RecordingJournal(ITransactionJournal inner) : ITransactionJournal
{
    private readonly List<TransactionStatus> _statuses = [];

    public IReadOnlyList<TransactionStatus> Statuses => _statuses;

    public Task AppendAsync(TransactionContext context, CancellationToken cancellationToken)
    {
        _statuses.Add(context.Status);
        return inner.AppendAsync(context, cancellationToken);
    }

    public Task<IReadOnlyList<TransactionContext>> ReadIncompleteAsync(CancellationToken cancellationToken) =>
        inner.ReadIncompleteAsync(cancellationToken);

    public Task<IReadOnlyList<TransactionContext>> ReadAllAsync(CancellationToken cancellationToken) =>
        inner.ReadAllAsync(cancellationToken);

    public Task<TransactionContext?> TryGetAsync(Guid transactionId, CancellationToken cancellationToken) =>
        inner.TryGetAsync(transactionId, cancellationToken);
}
