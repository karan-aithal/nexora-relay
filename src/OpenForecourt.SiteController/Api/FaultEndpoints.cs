using Microsoft.AspNetCore.Http.HttpResults;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Faults;
using OpenForecourt.SiteController.Orchestration;
using OpenForecourt.SiteController.Simulation;

namespace OpenForecourt.SiteController.Api;

/// <summary>Body for the host-link fault controls.</summary>
/// <param name="Down">Sever (true) or restore (false) the acquirer link.</param>
/// <param name="LatencyMs">Artificial latency on every host exchange, in milliseconds.</param>
/// <param name="ResponseCode">Acquirer response code to force: <c>00</c>, <c>51</c>, <c>05</c>, <c>91</c> or null to clear.</param>
public sealed record HostFaultBody(bool? Down, int? LatencyMs, string? ResponseCode);

/// <summary>
/// The fault-injection console's API.
/// </summary>
/// <remarks>
/// Every endpoint here arms a real fault on a real path. Nothing fabricates an <i>outcome</i>: the
/// decline comes back from the host simulator, the CRC error is detected by the firmware, the
/// power cut kills a process, and the duplicate is caught by the acquirer's own STAN ledger. The
/// dashboard then shows the system's genuine response, which is the whole point of the feature
/// (CLAUDE.md Phase 6).
/// </remarks>
public static class FaultEndpoints
{
    /// <summary>Registers every <c>/api/v1/faults</c> endpoint.</summary>
    public static void MapFaultApi(this IEndpointRouteBuilder app)
    {
        var faults = app.MapGroup("/api/v1/faults").WithTags("Faults");

        faults.MapGet("/", (FaultInjector injector) => TypedResults.Ok(injector.Snapshot()))
            .WithSummary("The currently armed faults.");

        faults.MapPost("/host", (HostFaultBody body, FaultInjector injector, SiteOptions options) =>
            {
                if (body.Down is { } down)
                {
                    injector.SetHostLinkDown(down);
                }

                if (body.LatencyMs is { } latency)
                {
                    injector.SetHostLatency(latency);
                }

                if (body.ResponseCode is not null)
                {
                    injector.SetForcedResponsePan(HostResponsePans.PanFor(body.ResponseCode, options.TestPan));
                }

                return TypedResults.Ok(injector.Snapshot());
            })
            .WithSummary("Severs or slows the acquirer link, or forces a specific response code.");

        faults.MapPost("/suspend", (FaultInjector injector) =>
            {
                injector.Suspend();
                return TypedResults.Ok(injector.Snapshot());
            })
            .WithSummary("Freezes every authorisation between the journal write and the host request.");

        faults.MapPost("/resume", (FaultInjector injector) =>
            {
                injector.Resume();
                return TypedResults.Ok(injector.Snapshot());
            })
            .WithSummary("Releases authorisations held by /faults/suspend.");

        faults.MapPost("/pumps/{id:int}/corrupt-crc", Results<Ok<FaultState>, ProblemHttpResult> (
                int id, FaultInjector injector, PumpRegistry pumps) =>
            {
                if (!pumps.Exists(id))
                {
                    return TypedResults.Problem($"Pump {id} does not exist.", statusCode: StatusCodes.Status404NotFound);
                }

                injector.ArmCrcCorruption(id);
                return TypedResults.Ok(injector.Snapshot());
            })
            .WithSummary("Corrupts the next outbound OFP-1 frame after framing, so the firmware's CRC check rejects it.");

        faults.MapPost("/pumps/{id:int}/power/{state}", Results<Ok<FaultState>, ProblemHttpResult> (
                int id, string state, FaultInjector injector, PumpFleet fleet) =>
            {
                bool wanted = string.Equals(state, "on", StringComparison.OrdinalIgnoreCase);
                bool changed = wanted ? fleet.PowerOn(id) : fleet.PowerCut(id);
                return changed
                    ? TypedResults.Ok(injector.Snapshot())
                    : TypedResults.Problem(
                        $"Pump {id} is not running under the firmware fleet, or is already {state}.",
                        statusCode: StatusCodes.Status409Conflict);
            })
            .WithSummary("Kills or restarts a pump's firmware process — a real power cut.");

        faults.MapPost("/duplicate/{id:guid}", async Task<Results<Ok<DuplicateResult>, ProblemHttpResult>> (
                Guid id, ITransactionJournal journal, IHostConnection host, CancellationToken ct) =>
            {
                var tx = await journal.TryGetAsync(id, ct).ConfigureAwait(false);
                if (tx is null)
                {
                    return TypedResults.Problem($"Transaction {id} does not exist.", statusCode: StatusCodes.Status404NotFound);
                }

                // Re-send the identical financial request. The host simulator keys its ledger on
                // (terminal, STAN), so this is caught there and answered with response code 94,
                // "duplicate transmission" — the acquirer detects it, not the site controller.
                var response = await host.SendAsync(new FinancialRequest(tx.TransactionId, tx.Amount), ct).ConfigureAwait(false);
                return TypedResults.Ok(new DuplicateResult(
                    tx.TransactionId, response.Outcome.ToString(), response.AuthorisationCode));
            })
            .WithSummary("Re-sends an existing transaction to the acquirer to show duplicate detection.");
    }
}

/// <summary>The acquirer's answer to a deliberately duplicated request.</summary>
/// <param name="TransactionId">The transaction that was re-sent.</param>
/// <param name="Outcome">What the host returned the second time.</param>
/// <param name="AuthorisationCode">The authorisation code, if the host still returned one.</param>
public sealed record DuplicateResult(Guid TransactionId, string Outcome, string? AuthorisationCode);

/// <summary>
/// Maps a wanted acquirer response code to the reserved test PAN that provokes it.
/// </summary>
/// <remarks>
/// The host simulator's rules are PAN-keyed (<c>deploy/hostrules.json</c>), which is how a real
/// acquirer test harness works too. Forcing a decline therefore means presenting the card the
/// issuer declines — the response code is produced by the host, never manufactured locally.
/// Every PAN here is from a reserved test range (CLAUDE.md section 7.1).
/// </remarks>
public static class HostResponsePans
{
    /// <summary>Test PAN the host declines with 51, insufficient funds.</summary>
    public const string InsufficientFunds = "4000000000000051";

    /// <summary>Test PAN the host declines with 05, do not honour.</summary>
    public const string DoNotHonour = "4000000000000002";

    /// <summary>Test PAN the host declines with 54, expired card.</summary>
    public const string ExpiredCard = "5555555555554444";

    /// <summary>Test PAN the host never answers, so the terminal must time out.</summary>
    public const string NoResponse = "378282246310005";

    /// <summary>Response codes the console offers, each backed by a rule in <c>hostrules.json</c>.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["00", "05", "51", "54", "91"];

    /// <summary>Returns the PAN that provokes a response code, or the approving PAN.</summary>
    /// <remarks>
    /// <c>91</c> ("issuer inoperative") is produced here by the silent-PAN rule rather than by a
    /// literal 91 in the response: the host does not answer at all, so the site controller takes
    /// its no-response path — reversal advice, then offline mode. That is the behaviour a 91
    /// actually exercises on a terminal.
    /// </remarks>
    public static string PanFor(string responseCode, string approvingPan) => responseCode switch
    {
        "51" => InsufficientFunds,
        "05" => DoNotHonour,
        "54" => ExpiredCard,
        "91" => NoResponse,
        _ => approvingPan,
    };
}
