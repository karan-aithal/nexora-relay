using OpenForecourt.Abstractions.Domain;

namespace OpenForecourt.Abstractions.Ports;

/// <summary>
/// A crash-safe, append-oriented record of transaction lifecycle events.
/// </summary>
/// <remarks>
/// <para>
/// The production implementation is SQLite in WAL mode (CLAUDE.md section 5): it must
/// survive power loss mid-transaction and let the system replay incomplete transactions on
/// restart. The write-ordering guarantee is the point of this port — a record is durable
/// once <see cref="AppendAsync"/> completes, and recovery reads back exactly what was
/// committed, never a partial write.
/// </para>
/// <para>
/// The precise record schema is fleshed out in the phase that first persists real
/// transactions; Phase 0 fixes the contract shape only.
/// </para>
/// </remarks>
public interface ITransactionJournal
{
    /// <summary>
    /// Durably appends one transaction state record. On return, the record has survived to
    /// stable storage and will be seen by recovery even if the process crashes immediately.
    /// </summary>
    /// <param name="context">The transaction state to record.</param>
    /// <param name="cancellationToken">Cancels the append.</param>
    Task AppendAsync(TransactionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the latest recorded state of every transaction that is not in a terminal
    /// state — the set that recovery must resolve on restart.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<TransactionContext>> ReadIncompleteAsync(CancellationToken cancellationToken);

    /// <summary>Returns the latest recorded state of every transaction, newest first.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<TransactionContext>> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>Returns the latest recorded state of one transaction, or null if unknown.</summary>
    /// <param name="transactionId">The transaction to look up.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<TransactionContext?> TryGetAsync(Guid transactionId, CancellationToken cancellationToken);
}
