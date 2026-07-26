using Microsoft.AspNetCore.Http.HttpResults;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Contracts;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Api;

/// <summary>The request body for an authorisation.</summary>
/// <param name="AmountMinor">Amount to authorise, in minor units.</param>
/// <param name="Token">The card token; never a PAN.</param>
public sealed record AuthoriseBody(long AmountMinor, string? Token);

/// <summary>Maps the versioned REST API (CLAUDE.md Phase 5).</summary>
public static class ForecourtEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>Registers every <c>/api/v1</c> endpoint.</summary>
    public static void MapForecourtApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1").WithTags("Forecourt");

        v1.MapGet("/pumps", (PumpRegistry pumps) => TypedResults.Ok(pumps.Snapshot()))
            .WithSummary("Lists every pump and its live state.");

        v1.MapGet("/pumps/{id:int}", Results<Ok<PumpSnapshot>, ProblemHttpResult> (int id, PumpRegistry pumps) =>
                pumps.Get(id) is { } pump
                    ? TypedResults.Ok(pump)
                    : NotFound($"Pump {id} does not exist."))
            .WithSummary("Returns one pump's state.");

        v1.MapPost("/pumps/{id:int}/authorise", AuthoriseAsync)
            .WithSummary("Authorises fuel on a pump. Requires an Idempotency-Key header.");

        v1.MapPost("/pumps/{id:int}/cancel", Results<Ok, ProblemHttpResult> (
                int id, HttpContext http, PumpRegistry pumps) =>
            {
                if (http.Request.Headers[IdempotencyHeader].Count == 0)
                {
                    return MissingIdempotencyKey();
                }

                if (pumps.Get(id) is not { } pump)
                {
                    return NotFound($"Pump {id} does not exist.");
                }

                if (pump.State is PumpState.Idle)
                {
                    return TypedResults.Problem("Pump has no transaction to cancel.", statusCode: StatusCodes.Status409Conflict);
                }

                pumps.Clear(id);
                return TypedResults.Ok();
            })
            .WithSummary("Cancels an in-progress authorisation on a pump before dispensing.");

        v1.MapGet("/transactions", async (ITransactionJournal journal, CancellationToken ct) =>
            {
                var all = await journal.ReadAllAsync(ct).ConfigureAwait(false);
                return TypedResults.Ok(all.Select(TransactionView.From).ToArray());
            })
            .WithSummary("Lists transactions, newest first.");

        v1.MapGet("/transactions/{id:guid}", async Task<Results<Ok<TransactionView>, ProblemHttpResult>> (
                Guid id, ITransactionJournal journal, CancellationToken ct) =>
            {
                var tx = await journal.TryGetAsync(id, ct).ConfigureAwait(false);
                return tx is not null
                    ? TypedResults.Ok(TransactionView.From(tx))
                    : NotFound($"Transaction {id} does not exist.");
            })
            .WithSummary("Returns one transaction.");

        v1.MapGet("/totals", async (ITransactionJournal journal, OfflinePolicy offline, SiteOptions options, CancellationToken ct) =>
            {
                var all = await journal.ReadAllAsync(ct).ConfigureAwait(false);
                return TypedResults.Ok(TotalsView.From(all, offline.CurrentExposure, options.Currency));
            })
            .WithSummary("Returns site transaction totals.");
    }

    private static async Task<Results<Ok<AuthorisationResult>, ProblemHttpResult>> AuthoriseAsync(
        int id,
        AuthoriseBody body,
        HttpContext http,
        PumpRegistry pumps,
        TransactionService transactions,
        IdempotencyStore idempotency,
        SiteOptions options,
        CancellationToken cancellationToken)
    {
        if (http.Request.Headers[IdempotencyHeader] is not [{ Length: > 0 } key])
        {
            return MissingIdempotencyKey();
        }

        if (!pumps.Exists(id))
        {
            return NotFound($"Pump {id} does not exist.");
        }

        if (body.AmountMinor <= 0)
        {
            return TypedResults.Problem("Amount must be positive.", statusCode: StatusCodes.Status400BadRequest);
        }

        var request = new AuthoriseRequest(
            id, new Money(body.AmountMinor, options.Currency), body.Token, CorrelationIdAccessor.Get(http));
        var result = await idempotency.GetOrAddAsync(key, () => transactions.AuthoriseAsync(request, cancellationToken))
            .ConfigureAwait(false);
        return TypedResults.Ok(result);
    }

    private static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult MissingIdempotencyKey() =>
        TypedResults.Problem($"An {IdempotencyHeader} header is required.", statusCode: StatusCodes.Status400BadRequest);
}
