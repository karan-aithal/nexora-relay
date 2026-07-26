using Microsoft.AspNetCore.Http.HttpResults;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Contracts;
using OpenForecourt.SiteController.Opt;
using OpenForecourt.SiteController.Orchestration;
using OpenForecourt.SiteController.Simulation;
using OpenForecourt.SiteController.Tracing;

namespace OpenForecourt.SiteController.Api;

/// <summary>Body for presenting a card at an OPT.</summary>
/// <param name="CardId">A profile id from the card catalogue.</param>
/// <param name="AmountMinor">The pre-authorisation amount, in minor units.</param>
/// <param name="EntryMode">How the card was presented: <c>Contact</c>, <c>Contactless</c> or <c>MagstripeFallback</c>.</param>
public sealed record PresentCardBody(string CardId, long AmountMinor, string? EntryMode);

/// <summary>Body for the OPT PIN pad.</summary>
/// <param name="Pin">The digits typed by the customer.</param>
public sealed record PinBody(string Pin);

/// <summary>Body for a flow-rate change.</summary>
/// <param name="MillilitresPerTick">Millilitres the meter delivers per tick.</param>
public sealed record FlowBody(int MillilitresPerTick);

/// <summary>Body for a grade change.</summary>
/// <param name="GradeCode">The grade code to select.</param>
public sealed record GradeBody(string GradeCode);

/// <summary>
/// The operator-side API: the forecourt simulator panel, the card simulator and the transaction
/// trace. These drive the same components a real forecourt's hardware would (CLAUDE.md Phase 6).
/// </summary>
public static class SimulatorEndpoints
{
    /// <summary>Registers every <c>/api/v1</c> simulator and OPT endpoint.</summary>
    public static void MapSimulatorApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        // --- site metadata ---------------------------------------------------------------
        v1.MapGet("/site", (SiteOptions options, GradeCatalogue grades, CardCatalogue cards, PumpFleet fleet) =>
                TypedResults.Ok(new SiteInfoView(
                    options.Currency,
                    options.PumpCount,
                    options.FloorLimitMinor,
                    fleet.IsActive,
                    [.. grades.All.Select(g => new GradeView(g.Code, g.Name, g.PricePerLitreMinor))],
                    cards.Views)))
            .WithTags("Site")
            .WithSummary("Static site configuration: currency, pumps, grades and card profiles.");

        // --- outdoor payment terminal ----------------------------------------------------
        var opt = v1.MapGroup("/opt").WithTags("OPT");

        opt.MapGet("/", (OptService service) => TypedResults.Ok(service.Snapshot()))
            .WithSummary("The screen state of every outdoor payment terminal.");

        opt.MapPost("/{id:int}/card", async Task<Results<Ok<OptResult>, ProblemHttpResult>> (
                int id, PresentCardBody body, OptService service, CancellationToken ct) =>
            {
                if (body.AmountMinor <= 0)
                {
                    return Problem("The pre-authorisation amount must be positive.", StatusCodes.Status400BadRequest);
                }

                var result = await service
                    .PresentCardAsync(id, body.CardId, body.AmountMinor, body.EntryMode ?? "Contact", ct)
                    .ConfigureAwait(false);
                return TypedResults.Ok(result);
            })
            .WithSummary("Presents a test card at a pump and runs the EMV flow to a decision.");

        opt.MapPost("/{id:int}/pin", async Task<Results<Ok<OptResult>, ProblemHttpResult>> (
                int id, PinBody body, OptService service, CancellationToken ct) =>
                TypedResults.Ok(await service.EnterPinAsync(id, body.Pin ?? "", ct).ConfigureAwait(false)))
            .WithSummary("Submits the online PIN for a terminal that is waiting for one.");

        opt.MapPost("/{id:int}/cancel", (int id, OptService service) =>
            {
                service.Cancel(id);
                return TypedResults.Ok(service.Get(id));
            })
            .WithSummary("Cancels the card session at a terminal.");

        opt.MapPost("/{id:int}/idle", (int id, OptService service) =>
            {
                service.ReturnToIdle(id);
                return TypedResults.Ok(service.Get(id));
            })
            .WithSummary("Returns a terminal to its idle screen and media loop.");

        // --- forecourt simulator panel ---------------------------------------------------
        var sim = v1.MapGroup("/simulator").WithTags("Simulator");

        sim.MapPost("/pumps/{id:int}/nozzle/{action}", Results<Ok, ProblemHttpResult> (
                int id, string action, PumpFleet fleet, PumpRegistry pumps) =>
            {
                if (!pumps.Exists(id))
                {
                    return Problem($"Pump {id} does not exist.", StatusCodes.Status404NotFound);
                }

                switch (action.ToUpperInvariant())
                {
                    case "UP":
                        fleet.NozzleUp(id);
                        return TypedResults.Ok();
                    case "DOWN":
                        fleet.NozzleDown(id);
                        return TypedResults.Ok();
                    default:
                        return Problem("The nozzle action must be 'up' or 'down'.", StatusCodes.Status400BadRequest);
                }
            })
            .WithSummary("Lifts or holsters a pump's nozzle. Holstering mid-dispense stops the fuelling.");

        sim.MapPost("/pumps/{id:int}/flow", Results<Ok, ProblemHttpResult> (int id, FlowBody body, PumpFleet fleet, PumpRegistry pumps) =>
            {
                if (!pumps.Exists(id))
                {
                    return Problem($"Pump {id} does not exist.", StatusCodes.Status404NotFound);
                }

                fleet.SetFlow(id, body.MillilitresPerTick);
                return TypedResults.Ok();
            })
            .WithSummary("Sets a pump's flow rate in millilitres per flow-meter tick.");

        sim.MapPost("/pumps/{id:int}/grade", Results<Ok, ProblemHttpResult> (int id, GradeBody body, PumpFleet fleet, PumpRegistry pumps) =>
            {
                if (!pumps.Exists(id))
                {
                    return Problem($"Pump {id} does not exist.", StatusCodes.Status404NotFound);
                }

                return fleet.SetGrade(id, body.GradeCode ?? "")
                    ? TypedResults.Ok()
                    : Problem($"No grade '{body.GradeCode}'.", StatusCodes.Status400BadRequest);
            })
            .WithSummary("Selects a fuel grade, pushing its unit price into the dispenser firmware.");

        sim.MapPost("/pumps/{id:int}/suspend", async (int id, PumpFleet fleet, CancellationToken ct) =>
            {
                await fleet.SuspendAsync(id, ct).ConfigureAwait(false);
                return TypedResults.Ok();
            })
            .WithSummary("Pauses an in-progress dispense.");

        sim.MapPost("/pumps/{id:int}/resume", async (int id, PumpFleet fleet, CancellationToken ct) =>
            {
                await fleet.ResumeAsync(id, ct).ConfigureAwait(false);
                return TypedResults.Ok();
            })
            .WithSummary("Resumes a paused dispense.");

        // --- traces ----------------------------------------------------------------------
        v1.MapGet("/transactions/{id:guid}/trace", Results<Ok<TransactionTraceView>, ProblemHttpResult> (Guid id, TraceStore traces) =>
                traces.TryGet(id) is { } trace
                    ? TypedResults.Ok(trace)
                    : Problem($"No trace retained for transaction {id}.", StatusCodes.Status404NotFound))
            .WithTags("Forecourt")
            .WithSummary("The full trace of a transaction: APDU exchange, ISO 8583 messages, pump commands, timings.");
    }

    private static ProblemHttpResult Problem(string detail, int statusCode) =>
        TypedResults.Problem(detail, statusCode: statusCode);
}
