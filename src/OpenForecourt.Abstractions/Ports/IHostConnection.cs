using OpenForecourt.Abstractions.Domain;

namespace OpenForecourt.Abstractions.Ports;

/// <summary>
/// A request for online authorisation sent to the acquiring host. Phase 0 shape; the
/// ISO 8583 field mapping is defined in Phase 1.
/// </summary>
/// <param name="TransactionId">Correlates the response to the originating transaction.</param>
/// <param name="Amount">The amount to authorise.</param>
public readonly record struct FinancialRequest(Guid TransactionId, Money Amount);

/// <summary>The host's decision on a <see cref="FinancialRequest"/>.</summary>
public enum AuthorisationOutcome
{
    /// <summary>The transaction was approved.</summary>
    Approved,

    /// <summary>The transaction was declined by the issuer/host.</summary>
    Declined,

    /// <summary>No usable response was obtained (timeout or unparseable reply).</summary>
    NoResponse,
}

/// <summary>
/// The host's response to a financial request. Phase 0 shape; carries only the decision
/// and an optional authorisation code for now.
/// </summary>
/// <param name="Outcome">The authorisation decision.</param>
/// <param name="AuthorisationCode">The issuer authorisation code when approved; otherwise null.</param>
public readonly record struct HostResponse(AuthorisationOutcome Outcome, string? AuthorisationCode);

/// <summary>
/// The link to the acquiring host. The real implementation sends length-prefixed
/// ISO 8583 over TCP; an in-process implementation is used for CI.
/// </summary>
/// <remarks>
/// <b>Timeout vs late response.</b> If no response arrives before the configured timeout,
/// <see cref="SendAsync"/> completes with <see cref="AuthorisationOutcome.NoResponse"/>
/// (it does not throw for a plain timeout). A response that arrives <i>after</i> the
/// timeout — a late response — must never be treated as authorising the (already
/// abandoned) request: implementations must correlate replies to requests and discard, or
/// route to reversal handling, any reply whose request has already timed out. Callers that
/// receive <see cref="AuthorisationOutcome.NoResponse"/> must assume the transaction state
/// at the host is unknown and reverse accordingly.
/// </remarks>
public interface IHostConnection
{
    /// <summary>Sends a financial request and awaits the host response or timeout.</summary>
    /// <param name="request">The authorisation request.</param>
    /// <param name="cancellationToken">Cancels the send/receive.</param>
    /// <returns>The host response, or one carrying <see cref="AuthorisationOutcome.NoResponse"/> on timeout.</returns>
    Task<HostResponse> SendAsync(FinancialRequest request, CancellationToken cancellationToken);
}
