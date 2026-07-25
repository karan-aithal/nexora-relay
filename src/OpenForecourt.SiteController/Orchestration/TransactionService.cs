using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Config;

namespace OpenForecourt.SiteController.Orchestration;

/// <summary>A request to authorise fuel on a pump.</summary>
/// <param name="PumpId">The pump.</param>
/// <param name="Amount">The amount to authorise.</param>
/// <param name="Token">The card token; never a PAN.</param>
/// <param name="CorrelationId">Trace id flowing from the OPT through to the host.</param>
public readonly record struct AuthoriseRequest(int PumpId, Money Amount, string? Token, Guid CorrelationId);

/// <summary>
/// The transaction orchestrator: it owns the money lifecycle and every failure path
/// (CLAUDE.md Phase 5). It is deliberately free of ASP.NET and SignalR types so its logic —
/// journal-first ordering, offline fallback, reversal and recovery — is unit-testable
/// against the in-process ports.
/// </summary>
/// <remarks>
/// The single invariant that makes crash recovery possible: <b>the intent is journalled
/// before the host request is sent</b>, and every subsequent lifecycle step is journalled
/// before its side effect is externally observable. On restart, <see cref="ResumeAsync"/>
/// resolves whatever state a transaction was left in.
/// </remarks>
public sealed class TransactionService(
    ITransactionJournal journal,
    IHostConnection host,
    ITransactionDispatch dispatch,
    HostAvailability availability,
    OfflinePolicy offlinePolicy,
    PumpRegistry pumps,
    StanSequence stans,
    IClock clock,
    SiteOptions options,
    ILogger<TransactionService> logger)
{
    /// <summary>Raised on every journalled state change, for the SignalR lifecycle feed.</summary>
    public event EventHandler<TransactionContext>? TransactionUpdated;

    /// <summary>
    /// Runs a full authorisation. Writes the intent, then either goes online to the host or,
    /// if the host is down, decides offline under the floor limit.
    /// </summary>
    public async Task<AuthorisationResult> AuthoriseAsync(AuthoriseRequest request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var tx = TransactionContext.NewIntent(
            Guid.CreateVersion7(), request.PumpId, request.Amount, request.Token, request.CorrelationId, now);

        pumps.SetState(request.PumpId, PumpState.Authorising, tx.TransactionId, request.Amount);
        await WriteAsync(tx, cancellationToken).ConfigureAwait(false); // POINT 1: intent durable before any host contact

        if (!availability.IsAvailable)
        {
            return await AuthoriseOfflineAsync(tx, cancellationToken).ConfigureAwait(false);
        }

        tx = tx with { Status = TransactionStatus.HostRequestSent, Stan = stans.Next(), UpdatedAt = clock.UtcNow };
        await WriteAsync(tx, cancellationToken).ConfigureAwait(false); // POINT 2: request-sent recorded before the wire

        var response = await host.SendAsync(new FinancialRequest(tx.TransactionId, tx.Amount), cancellationToken)
            .ConfigureAwait(false);

        switch (response.Outcome)
        {
            case AuthorisationOutcome.Approved:
                availability.MarkAvailable();
                return await CompleteApprovalAsync(tx, response.AuthorisationCode, offline: false, cancellationToken)
                    .ConfigureAwait(false);

            case AuthorisationOutcome.Declined:
                availability.MarkAvailable();
                return await DeclineAsync(tx, cancellationToken).ConfigureAwait(false);

            default: // NoResponse: host state unknown -> reverse, and fall back to offline for the next customer
                logger.LogWarning("No host response for {TransactionId}; reversing.", tx.TransactionId);
                availability.MarkUnavailable();
                await ReverseAsync(tx, cancellationToken).ConfigureAwait(false);
                return new AuthorisationResult(tx.TransactionId, AuthorisationDecision.Reversed, null);
        }
    }

    /// <summary>
    /// Resolves one incomplete transaction found in the journal at startup (CLAUDE.md Phase 5,
    /// "On startup, scan for in-flight records and reconcile").
    /// </summary>
    public async Task ResumeAsync(TransactionContext tx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        switch (tx.Status)
        {
            case TransactionStatus.Intent:
                // Nothing was ever sent to the host: safe to abandon.
                await WriteAsync(tx with { Status = TransactionStatus.Voided, State = PumpState.Idle, UpdatedAt = clock.UtcNow },
                    cancellationToken).ConfigureAwait(false);
                pumps.Clear(tx.PumpId);
                logger.LogInformation("Recovery: voided intent-only transaction {TransactionId}.", tx.TransactionId);
                break;

            case TransactionStatus.HostRequestSent:
                // The host may or may not have authorised: its state is unknown, so reverse.
                await ReverseAsync(tx, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Recovery: reversed in-flight transaction {TransactionId}.", tx.TransactionId);
                break;

            case TransactionStatus.Approved when tx.Offline:
                // An offline approval awaiting replay: re-track its exposure and leave it for the replayer.
                offlinePolicy.Restore(tx.Amount.Minor);
                logger.LogInformation("Recovery: re-queued offline transaction {TransactionId} for replay.", tx.TransactionId);
                break;

            case TransactionStatus.Approved:
                // Online approval that never settled: finish it.
                await SettleAsync(tx, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Recovery: resumed settlement of {TransactionId}.", tx.TransactionId);
                break;

            default:
                break; // terminal states need no work
        }
    }

    /// <summary>
    /// Replays one offline-approved transaction to the host now that it is reachable. Approved
    /// replays settle; declined replays generate a reversal/exception (CLAUDE.md Phase 5).
    /// </summary>
    public async Task ReplayOfflineAsync(TransactionContext tx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        var response = await host.SendAsync(new FinancialRequest(tx.TransactionId, tx.Amount), cancellationToken)
            .ConfigureAwait(false);

        switch (response.Outcome)
        {
            case AuthorisationOutcome.Approved:
                offlinePolicy.Release(tx.Amount.Minor);
                await SettleAsync(tx with { AuthorisationCode = response.AuthorisationCode ?? tx.AuthorisationCode },
                    cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Replay: offline transaction {TransactionId} confirmed and settled.", tx.TransactionId);
                break;

            case AuthorisationOutcome.Declined:
                offlinePolicy.Release(tx.Amount.Minor);
                await ReverseAsync(tx, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("Replay: offline transaction {TransactionId} declined; wrote reversal/exception.", tx.TransactionId);
                break;

            default:
                availability.MarkUnavailable(); // host dropped again mid-replay; leave the record for the next round
                logger.LogWarning("Replay: host unavailable again for {TransactionId}; will retry.", tx.TransactionId);
                break;
        }
    }

    private async Task<AuthorisationResult> AuthoriseOfflineAsync(TransactionContext tx, CancellationToken cancellationToken)
    {
        if (tx.Amount.Minor <= options.FloorLimitMinor && offlinePolicy.TryReserve(tx.Amount.Minor))
        {
            logger.LogInformation("Host down: authorising {TransactionId} offline for {Amount}.", tx.TransactionId, tx.Amount.Minor);
            return await CompleteApprovalAsync(tx, authorisationCode: "OFFLINE", offline: true, cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogInformation("Host down: declining {TransactionId} (over floor limit or exposure ceiling).", tx.TransactionId);
        return await DeclineAsync(tx, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthorisationResult> CompleteApprovalAsync(
        TransactionContext tx, string? authorisationCode, bool offline, CancellationToken cancellationToken)
    {
        tx = tx with
        {
            Status = TransactionStatus.Approved,
            State = PumpState.Authorised,
            AuthorisationCode = authorisationCode,
            Offline = offline,
            UpdatedAt = clock.UtcNow,
        };
        await WriteAsync(tx, cancellationToken).ConfigureAwait(false); // POINT 3: approval durable before settlement
        pumps.SetState(tx.PumpId, PumpState.Authorised, tx.TransactionId, tx.Amount);

        if (offline)
        {
            // Host is down: the transaction is queued (as Approved+Offline) for later replay.
            return new AuthorisationResult(tx.TransactionId, AuthorisationDecision.ApprovedOffline, authorisationCode);
        }

        await SettleAsync(tx, cancellationToken).ConfigureAwait(false);
        return new AuthorisationResult(tx.TransactionId, AuthorisationDecision.Approved, authorisationCode);
    }

    private async Task<AuthorisationResult> DeclineAsync(TransactionContext tx, CancellationToken cancellationToken)
    {
        await WriteAsync(tx with { Status = TransactionStatus.Declined, State = PumpState.Idle, UpdatedAt = clock.UtcNow },
            cancellationToken).ConfigureAwait(false);
        pumps.Clear(tx.PumpId);
        return new AuthorisationResult(tx.TransactionId, AuthorisationDecision.Declined, null);
    }

    private async Task SettleAsync(TransactionContext tx, CancellationToken cancellationToken)
    {
        // Store-and-forward first: the settlement is durable on the queue before we mark the
        // transaction complete, so a crash here re-settles on recovery (the consumer dedupes).
        await dispatch.PublishAsync(
            new DispatchMessage(tx.TransactionId, "settlement", Payload(tx)), cancellationToken).ConfigureAwait(false);
        await WriteAsync(tx with { Status = TransactionStatus.Completed, State = PumpState.Idle, UpdatedAt = clock.UtcNow },
            cancellationToken).ConfigureAwait(false); // POINT 5: completion
        pumps.Clear(tx.PumpId);
    }

    private async Task ReverseAsync(TransactionContext tx, CancellationToken cancellationToken)
    {
        await dispatch.PublishAsync(
            new DispatchMessage(tx.TransactionId, "reversal-advice", Payload(tx)), cancellationToken).ConfigureAwait(false);
        await WriteAsync(tx with { Status = TransactionStatus.Reversed, State = PumpState.Idle, UpdatedAt = clock.UtcNow },
            cancellationToken).ConfigureAwait(false);
        pumps.Clear(tx.PumpId);
    }

    private async Task WriteAsync(TransactionContext tx, CancellationToken cancellationToken)
    {
        await journal.AppendAsync(tx, cancellationToken).ConfigureAwait(false);
        TransactionUpdated?.Invoke(this, tx);
    }

    private static string Payload(TransactionContext tx) => JsonSerializer.Serialize(new
    {
        transactionId = tx.TransactionId,
        amountMinor = tx.Amount.Minor,
        currency = tx.Amount.CurrencyCode,
        offline = tx.Offline,
        authorisationCode = tx.AuthorisationCode,
    });
}
