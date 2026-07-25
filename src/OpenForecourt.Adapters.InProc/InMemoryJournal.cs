using System.Collections.Concurrent;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Adapters.InProc;

/// <summary>
/// An <see cref="ITransactionJournal"/> held entirely in memory: the CI stand-in for
/// <c>SqliteJournal</c>, and the same port, so recovery and orchestration logic can be
/// tested without touching a file (CLAUDE.md section 3).
/// </summary>
/// <remarks>
/// It is <b>not</b> crash-safe — nothing here survives process exit — so it is for tests
/// and in-memory demos only. Writes are idempotent upserts keyed by transaction id, exactly
/// like the SQLite journal, so code that works against one works against the other.
/// </remarks>
public sealed class InMemoryJournal : ITransactionJournal
{
    private readonly ConcurrentDictionary<Guid, TransactionContext> _records = new();

    /// <inheritdoc />
    public Task AppendAsync(TransactionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _records[context.TransactionId] = context;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TransactionContext>> ReadIncompleteAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TransactionContext> result = _records.Values
            .Where(r => !r.Status.IsTerminal())
            .OrderBy(r => r.CreatedAt)
            .ToArray();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TransactionContext>> ReadAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TransactionContext> result = _records.Values
            .OrderByDescending(r => r.CreatedAt)
            .ToArray();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<TransactionContext?> TryGetAsync(Guid transactionId, CancellationToken cancellationToken) =>
        Task.FromResult(_records.TryGetValue(transactionId, out var record) ? record : null);
}
