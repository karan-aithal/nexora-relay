namespace OpenForecourt.Abstractions.Domain;

/// <summary>
/// The mutable-over-time facts about a single forecourt transaction as it moves through
/// its lifecycle. Carried across component boundaries and written to the journal.
/// </summary>
/// <remarks>
/// This is a Phase 0 shape: fields will grow (amounts, token reference, auth codes) in
/// later phases. It deliberately holds a <see cref="CorrelationId"/> distinct from
/// <see cref="TransactionId"/> so a single logical customer interaction can be traced
/// across OPT, pump manager, site controller and host even if it spawns retries.
/// </remarks>
/// <param name="TransactionId">Stable unique identifier for this transaction.</param>
/// <param name="PumpId">The pump this transaction belongs to.</param>
/// <param name="State">The current lifecycle state.</param>
/// <param name="CreatedAt">When the transaction was opened (from <see cref="Ports.IClock"/>).</param>
/// <param name="UpdatedAt">When the state was last changed.</param>
/// <param name="CorrelationId">Trace identifier spanning all components for this interaction.</param>
public sealed record TransactionContext(
    Guid TransactionId,
    int PumpId,
    PumpState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid CorrelationId);
