namespace OpenForecourt.SiteController.Orchestration;

/// <summary>The decision an authorisation attempt reached.</summary>
public enum AuthorisationDecision
{
    /// <summary>Approved online by the host.</summary>
    Approved,

    /// <summary>Approved offline under the floor limit while the host was unreachable.</summary>
    ApprovedOffline,

    /// <summary>Declined (by the host, or offline because it exceeded a limit).</summary>
    Declined,

    /// <summary>No usable host response; the transaction was reversed and its state is unknown.</summary>
    Reversed,
}

/// <summary>The outcome of an authorisation request.</summary>
/// <param name="TransactionId">The transaction that was opened.</param>
/// <param name="Decision">What was decided.</param>
/// <param name="AuthorisationCode">The authorisation code when approved; otherwise null.</param>
public sealed record AuthorisationResult(Guid TransactionId, AuthorisationDecision Decision, string? AuthorisationCode);
