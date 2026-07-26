using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.PumpManager;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Faults;
using OpenForecourt.SiteController.Orchestration;
using OpenForecourt.SiteController.Tracing;

namespace OpenForecourt.SiteController.Simulation;

/// <summary>
/// Puts the real pump firmware on the orchestration path.
/// </summary>
/// <remarks>
/// <para>
/// One <c>pump_host</c> process per pump — the identical portable C core that Phase 4 also
/// cross-compiles for a Cortex-M4 — connected over TCP and driven with OFP-1 by
/// <see cref="PumpSession"/>. The volume and value on the dashboard are therefore metered by
/// firmware and carried over a framed, CRC-checked, sequence-correlated protocol; nothing about
/// the fuelling is animated by the web tier.
/// </para>
/// <para>
/// The fleet also owns the *physical world*. The firmware host is launched with
/// <c>--manual 1</c>, which stands its scripted customer down, and the nozzle, flow rate and unit
/// price are then driven over its stdin control channel. Those commands land on the same
/// <c>ofp_pump_nozzle_up</c> / <c>_on_pulses</c> entry points a sensor ISR would call — they are a
/// second source of physical input, never a back door into the protocol. "Power-cut a pump" is
/// exactly that: the process is killed, and the transport's reconnect loop is left to cope.
/// </para>
/// <para>
/// When <see cref="SiteOptions.PumpFirmwarePath"/> is null the fleet does not start at all, which
/// is how CI and the Phase 5 tests keep running with no firmware binary present.
/// </para>
/// </remarks>
public sealed class PumpFleet(
    SiteOptions options,
    PumpRegistry pumps,
    TransactionService transactions,
    FaultInjector faults,
    TraceStore traces,
    GradeCatalogue grades,
    IClock clock,
    ILogger<PumpFleet> logger) : IHostedService, IAsyncDisposable
{
    private readonly Dictionary<int, Pump> _pumps = [];
    private PumpManagerService? _manager;

    /// <summary>True when firmware pumps are configured and running.</summary>
    public bool IsActive => _manager is not null;

    /// <summary>The pump ids under management.</summary>
    public IReadOnlyCollection<int> PumpIds => _pumps.Keys;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.PumpFirmwarePath is not { Length: > 0 } firmware || !File.Exists(firmware))
        {
            logger.LogInformation(
                "Pump fleet not started: no firmware at '{Path}'. Pump state stays journal-driven.",
                options.PumpFirmwarePath ?? "(unset)");
            return;
        }

        var sessions = new List<PumpSession>();
        for (int id = 1; id <= Math.Min(options.PumpCount, PumpManagerService.MaxPumps); id++)
        {
            var grade = grades.Default;
            var pump = new Pump(id, options.PumpBasePort + id - 1, firmware, grade.Code, options.PumpFlowMlPerTick);
            pump.Process = Launch(pump, grade.PricePerLitreMinor);

            // The wire mutator is the CRC-fault seam: it corrupts the framed bytes on the way out,
            // so the firmware's CRC check is what rejects the frame (docs/protocol-pump.md §5).
            pump.Transport = new TcpPumpTransport(
                new IPEndPoint(IPAddress.Loopback, pump.Port), clock,
                log: line => logger.LogDebug("pump {PumpId} link: {Line}", pump.Id, line),
                wireMutator: wire => Corrupt(pump.Id, wire));
            pump.Transport.StateChanged += (_, state) => OnLinkChanged(pump.Id, state);

            pump.Session = new PumpSession(pump.Id, pump.Transport, clock);
            pump.Session.Observed += (_, notification) => OnObserved(pump, notification);
            sessions.Add(pump.Session);

            _pumps[id] = pump;
            pumps.Update(id, s => s with
            {
                GradeCode = grade.Code,
                UnitPricePerLitreMinor = grade.PricePerLitreMinor,
                Link = "Connecting",
            });
        }

        _manager = new PumpManagerService(sessions);
        _manager.Start();
        logger.LogInformation("Pump fleet started: {Count} firmware pumps from port {Port}.", _pumps.Count, options.PumpBasePort);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_manager is not null)
        {
            await _manager.StopAsync().ConfigureAwait(false);
        }

        foreach (var pump in _pumps.Values)
        {
            KillProcess(pump);
        }
    }

    /// <summary>Authorises the dispenser after the acquirer approved, with a value preset.</summary>
    public async Task<bool> AuthoriseAsync(int pumpId, PresetMode mode, uint limit, CancellationToken cancellationToken)
    {
        if (!_pumps.TryGetValue(pumpId, out var pump) || pump.Session is null)
        {
            return false;
        }

        var response = await pump.Session.AuthoriseAsync(mode, limit, cancellationToken).ConfigureAwait(false);
        RecordPumpStep(pump, $"AUTHORISE {mode} {limit} → {response.Status}");
        return response.Status == CommandStatus.Ack;
    }

    /// <summary>Cancels an authorisation, or acknowledges a completed dispense / latched fault.</summary>
    public async Task CancelAsync(int pumpId, CancellationToken cancellationToken)
    {
        if (_pumps.TryGetValue(pumpId, out var pump) && pump.Session is not null)
        {
            var response = await pump.Session.CancelAsync(cancellationToken).ConfigureAwait(false);
            RecordPumpStep(pump, $"CANCEL_AUTH → {response.Status}");
        }
    }

    /// <summary>Pauses an in-progress dispense.</summary>
    public Task SuspendAsync(int pumpId, CancellationToken cancellationToken) =>
        _pumps.TryGetValue(pumpId, out var pump) && pump.Session is not null
            ? pump.Session.SuspendAsync(cancellationToken)
            : Task.FromResult(default(CommandResponse));

    /// <summary>Resumes a paused dispense.</summary>
    public Task ResumeAsync(int pumpId, CancellationToken cancellationToken) =>
        _pumps.TryGetValue(pumpId, out var pump) && pump.Session is not null
            ? pump.Session.ResumeAsync(cancellationToken)
            : Task.FromResult(default(CommandResponse));

    /// <summary>Lifts the nozzle (operator panel / customer action).</summary>
    public void NozzleUp(int pumpId) => Control(pumpId, "up");

    /// <summary>Holsters the nozzle. Mid-dispense this is the "drop nozzle" fault.</summary>
    public void NozzleDown(int pumpId) => Control(pumpId, "down");

    /// <summary>Sets the flow rate in millilitres per meter tick.</summary>
    public void SetFlow(int pumpId, int millilitresPerTick)
    {
        int flow = Math.Clamp(millilitresPerTick, 1, 2000);
        Control(pumpId, $"flow {flow.ToString(CultureInfo.InvariantCulture)}");
        if (_pumps.TryGetValue(pumpId, out var pump))
        {
            pump.FlowMlPerTick = flow;
        }
    }

    /// <summary>Selects a grade: pushes its unit price into the dispenser and onto the tile.</summary>
    public bool SetGrade(int pumpId, string gradeCode)
    {
        if (!_pumps.TryGetValue(pumpId, out var pump) || grades.TryGet(gradeCode) is not { } grade)
        {
            return false;
        }

        pump.GradeCode = grade.Code;
        Control(pumpId, $"price {grade.PricePerLitreMinor.ToString(CultureInfo.InvariantCulture)}");
        pumps.Update(pumpId, s => s with { GradeCode = grade.Code, UnitPricePerLitreMinor = grade.PricePerLitreMinor });
        return true;
    }

    /// <summary>Kills a pump's firmware process — a genuine power cut, mid-transaction or not.</summary>
    public bool PowerCut(int pumpId)
    {
        if (!_pumps.TryGetValue(pumpId, out var pump) || pump.Process is null)
        {
            return false;
        }

        logger.LogWarning("Fault injection: power-cutting pump {PumpId} (pid {Pid}).", pumpId, pump.Process.Id);
        KillProcess(pump);
        faults.SetPoweredOff(pumpId, true);
        pumps.Update(pumpId, s => s with { State = PumpState.OutOfService, Link = "Disconnected", DispenserState = "PowerOff" });
        return true;
    }

    /// <summary>Restores power to a pump: the process restarts and the transport reconnects itself.</summary>
    public bool PowerOn(int pumpId)
    {
        if (!_pumps.TryGetValue(pumpId, out var pump) || pump.Process is { HasExited: false })
        {
            return false;
        }

        var grade = grades.TryGet(pump.GradeCode) ?? grades.Default;
        pump.Process = Launch(pump, grade.PricePerLitreMinor);
        faults.SetPoweredOff(pumpId, false);
        pumps.Update(pumpId, s => s with { State = PumpState.Idle, DispenserState = "Idle" });
        logger.LogInformation("Pump {PumpId} powered back on; awaiting link.", pumpId);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var pump in _pumps.Values)
        {
            if (pump.Transport is not null)
            {
                await pump.Transport.DisposeAsync().ConfigureAwait(false);
            }

            pump.Process?.Dispose();
        }
    }

    private byte[] Corrupt(int pumpId, byte[] wire)
    {
        if (!faults.ConsumeCrcCorruption(pumpId) || wire.Length < 3)
        {
            return wire;
        }

        // Flip the byte immediately before ETX. That is inside the CRC's coverage, so the
        // firmware recomputes a different CRC and drops the frame; the manager then sees no
        // ACK, retransmits under its own timeout, and the retry succeeds (docs §4.2, §5).
        var corrupted = (byte[])wire.Clone();
        corrupted[^2] ^= 0xFF;
        logger.LogWarning("Fault injection: corrupted the outbound frame CRC for pump {PumpId}.", pumpId);
        return corrupted;
    }

    private Process Launch(Pump pump, int pricePerLitreMinor)
    {
        var info = new ProcessStartInfo(pump.FirmwarePath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] arguments =
        [
            "--port", pump.Port.ToString(CultureInfo.InvariantCulture),
            "--manual", "1",
            "--price", pricePerLitreMinor.ToString(CultureInfo.InvariantCulture),
            "--tick-ms", options.PumpTickMs.ToString(CultureInfo.InvariantCulture),
            "--flow-ml-per-tick", pump.FlowMlPerTick.ToString(CultureInfo.InvariantCulture),
        ];
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {pump.FirmwarePath}.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 })
            {
                logger.LogDebug("pump {PumpId} firmware: {Line}", pump.Id, e.Data);
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private void Control(int pumpId, string command)
    {
        if (!_pumps.TryGetValue(pumpId, out var pump) || pump.Process is not { HasExited: false })
        {
            return;
        }

        // One writer at a time: the panel, the fault console and the OPT can all reach here.
        lock (pump.ControlGate)
        {
            try
            {
                pump.Process.StandardInput.WriteLine(command);
                pump.Process.StandardInput.Flush();
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Pump {PumpId} control channel closed.", pumpId);
            }
        }
    }

    private void OnLinkChanged(int pumpId, PumpConnectionState state) =>
        pumps.Update(pumpId, s => s with { Link = state.ToString() });

    private void OnObserved(Pump pump, PumpNotification notification)
    {
        var now = clock.UtcNow;

        // FLOW_UPDATE fires several times a second for the whole fuelling. It belongs on the
        // tile, not on the trace: a hundred identical rows would bury the exchanges that
        // actually explain the transaction.
        if (notification.Event != PumpEvent.FlowUpdate)
        {
            RecordPumpStep(pump, Describe(notification));
        }

        switch (notification.Event)
        {
            case PumpEvent.NozzleUp:
                pumps.Update(pump.Id, s => s with
                {
                    NozzleUp = true,
                    DispenserState = notification.State.ToString(),
                    State = s.TransactionId is null ? s.State : PumpState.NozzleLifted,
                    StartedAt = s.StartedAt ?? now,
                });
                break;

            case PumpEvent.FlowUpdate:
                pumps.Update(pump.Id, s => s with
                {
                    DispensedMillilitres = (int)notification.Volume.MilliLitres,
                    DispensedMinor = notification.Value.Minor,
                    DispenserState = notification.State.ToString(),
                    State = s.TransactionId is null ? s.State : PumpState.Dispensing,
                });
                break;

            case PumpEvent.NozzleDown:
                pumps.Update(pump.Id, s => s with { NozzleUp = false, DispenserState = notification.State.ToString() });
                break;

            case PumpEvent.DispenseComplete:
                _ = OnDispenseCompleteAsync(pump, notification);
                break;

            case PumpEvent.Fault:
                logger.LogWarning("Pump {PumpId} latched fault {Fault}.", pump.Id, notification.Fault);
                pumps.Update(pump.Id, s => s with { State = PumpState.Error, DispenserState = notification.State.ToString() });
                break;

            default:
                break;
        }

        if (notification.ProtocolViolation)
        {
            logger.LogError("Pump {PumpId} protocol violation: {Detail}", pump.Id, notification.Detail);
        }
    }

    // Fire-and-forget from the session's event, but every failure is logged: an unobserved
    // exception here would silently strand a transaction in Approved.
    private async Task OnDispenseCompleteAsync(Pump pump, PumpNotification notification)
    {
        try
        {
            var delivered = new Money(notification.Value.Minor, options.Currency);
            pumps.Update(pump.Id, s => s with
            {
                DispensedMillilitres = (int)notification.Volume.MilliLitres,
                DispensedMinor = delivered.Minor,
                DispenserState = notification.State.ToString(),
                State = s.TransactionId is null ? s.State : PumpState.DispenseComplete,
                NozzleUp = false,
            });

            var transactionId = pumps.Get(pump.Id)?.TransactionId;
            if (transactionId is { } id)
            {
                traces.TraceFor(id).Add(clock.UtcNow, TimeSpan.Zero, "pump", $"DISPENSE_COMPLETE {Litres(notification.Volume)} L");
                await transactions.CompleteFuellingAsync(id, delivered, CancellationToken.None).ConfigureAwait(false);
            }

            // Acknowledge the completion so the firmware clears COMPLETE and stops re-emitting (docs §4.4).
            // The ACK carries no state, so the mirrored FSM state is advanced here to match.
            await CancelAsync(pump.Id, CancellationToken.None).ConfigureAwait(false);
            pumps.Update(pump.Id, s => s with { DispenserState = pump.Session?.State.ToString() ?? "Idle" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to settle the completed dispense on pump {PumpId}.", pump.Id);
        }
    }

    private void RecordPumpStep(Pump pump, string label)
    {
        if (pumps.Get(pump.Id)?.TransactionId is { } id)
        {
            traces.TraceFor(id).Add(clock.UtcNow, TimeSpan.Zero, "pump", label);
        }
    }

    private static string Describe(PumpNotification n) => n.Event switch
    {
        PumpEvent.FlowUpdate => $"FLOW_UPDATE {Litres(n.Volume)} L / {n.Value.Minor} minor",
        PumpEvent.DispenseComplete => $"DISPENSE_COMPLETE {Litres(n.Volume)} L / {n.Value.Minor} minor",
        PumpEvent.Fault => $"FAULT {n.Fault}",
        _ => n.Event.ToString().ToUpperInvariant(),
    };

    private static string Litres(Volume volume) =>
        (volume.MilliLitres / 1000.0).ToString("F3", CultureInfo.InvariantCulture);

    private static void KillProcess(Pump pump)
    {
        try
        {
            if (pump.Process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone; nothing to kill.
        }
    }

    private sealed class Pump(int id, int port, string firmwarePath, string gradeCode, int flowMlPerTick)
    {
        public int Id { get; } = id;

        public int Port { get; } = port;

        public string FirmwarePath { get; } = firmwarePath;

        public Lock ControlGate { get; } = new();

        public string GradeCode { get; set; } = gradeCode;

        public int FlowMlPerTick { get; set; } = flowMlPerTick;

        public Process? Process { get; set; }

        public TcpPumpTransport? Transport { get; set; }

        public PumpSession? Session { get; set; }
    }
}
