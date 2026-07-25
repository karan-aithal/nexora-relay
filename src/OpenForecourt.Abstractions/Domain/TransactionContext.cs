namespace OpenForecourt.Abstractions.Domain;

/// <summary>
/// The mutable-over-time facts about a single forecourt transaction as it moves through
/// its lifecycle. Carried across component boundaries and written to the journal; the
/// journal keeps only the latest state per <see cref="TransactionId"/>.
/// </summary>
/// <remarks>
/// It deliberately holds a <see cref="CorrelationId"/> distinct from
/// <see cref="TransactionId"/> so a single logical customer interaction can be traced
/// across OPT, pump manager, site controller and host even if it spawns retries. Only a
/// <b>token</b> is ever carried here, never a PAN: the P2PE boundary is upstream
/// (CLAUDE.md section 7.4).
/// </remarks>
/// <param name="TransactionId">Stable unique identifier for this transaction.</param>
/// <param name="PumpId">The pump this transaction belongs to.</param>
/// <param name="State">The pump-facing lifecycle state (for the dashboard).</param>
/// <param name="Status">The crash-recovery lifecycle state (for the journal).</param>
/// <param name="Amount">The amount authorised/dispensed.</param>
/// <param name="Token">The surrogate token for the card; never a PAN.</param>
/// <param name="Stan">The system trace audit number used on the host request, when one was sent.</param>
/// <param name="AuthorisationCode">The issuer authorisation code once approved; otherwise null.</param>
/// <param name="Offline">True when this transaction was authorised offline under the floor limit.</param>
/// <param name="CreatedAt">When the transaction was opened (from <see cref="Ports.IClock"/>).</param>
/// <param name="UpdatedAt">When the state was last changed.</param>
/// <param name="CorrelationId">Trace identifier spanning all components for this interaction.</param>
public sealed record TransactionContext(
    Guid TransactionId,
    int PumpId,
    PumpState State,
    TransactionStatus Status,
    Money Amount,
    string? Token,
    string? Stan,
    string? AuthorisationCode,
    bool Offline,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid CorrelationId)
{
    /// <summary>
    /// Opens a fresh transaction in <see cref="TransactionStatus.Intent"/> — the record that
    /// must be journalled <b>before</b> any host request is sent.
    /// </summary>
    public static TransactionContext NewIntent(
        Guid transactionId,
        int pumpId,
        Money amount,
        string? token,
        Guid correlationId,
        DateTimeOffset now) =>
        new(transactionId, pumpId, PumpState.Authorising, TransactionStatus.Intent,
            amount, token, Stan: null, AuthorisationCode: null, Offline: false,
            CreatedAt: now, UpdatedAt: now, CorrelationId: correlationId);
}
