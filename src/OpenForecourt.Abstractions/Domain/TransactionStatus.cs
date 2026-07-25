namespace OpenForecourt.Abstractions.Domain;

/// <summary>
/// The crash-recovery lifecycle of a single transaction, as written to the journal.
/// </summary>
/// <remarks>
/// <para>
/// This is distinct from <see cref="PumpState"/>: <see cref="PumpState"/> is what the
/// dashboard shows, this is what recovery reasons about after a crash. The ordering is the
/// whole point of the journal — a record advances through these states and is durable at
/// each step, so restart can tell exactly how far a transaction got and what to do about it
/// (CLAUDE.md Phase 5, "Write intent before the host request is sent").
/// </para>
/// <para>
/// Recovery of a non-terminal record:
/// <list type="bullet">
/// <item><see cref="Intent"/> — no host contact was ever made; safe to <see cref="Voided"/>.</item>
/// <item><see cref="HostRequestSent"/> — the host state is <b>unknown</b>; reverse (send an advice) and mark <see cref="Reversed"/>.</item>
/// <item><see cref="Approved"/> — authorised but not finished; resume settlement to <see cref="Completed"/>, or if offline, keep queued for replay.</item>
/// </list>
/// </para>
/// </remarks>
public enum TransactionStatus
{
    /// <summary>Intent recorded before the host request was sent. No host contact yet.</summary>
    Intent,

    /// <summary>The authorisation request was sent to the host; no response recorded yet. Host state unknown.</summary>
    HostRequestSent,

    /// <summary>Authorised (online by the host, or offline under the floor limit) but not yet settled.</summary>
    Approved,

    /// <summary>The host declined the authorisation. Terminal.</summary>
    Declined,

    /// <summary>Settlement completed and dispatched. Terminal.</summary>
    Completed,

    /// <summary>A reversal/advice was generated (timeout, unknown host state, or declined replay). Terminal.</summary>
    Reversed,

    /// <summary>Abandoned before any host contact; nothing to reverse. Terminal.</summary>
    Voided,
}

/// <summary>Extensions over <see cref="TransactionStatus"/>.</summary>
public static class TransactionStatusExtensions
{
    /// <summary>
    /// True when no further work is owed for this transaction — recovery skips terminal records.
    /// </summary>
    public static bool IsTerminal(this TransactionStatus status) => status is
        TransactionStatus.Declined or
        TransactionStatus.Completed or
        TransactionStatus.Reversed or
        TransactionStatus.Voided;
}
