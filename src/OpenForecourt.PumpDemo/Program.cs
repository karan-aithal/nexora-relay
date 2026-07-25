// Phase 4 demo. Spawns four pump-firmware host processes, connects the pump manager to each
// over TCP, then: runs a full fuelling on two pumps concurrently with a live frame trace,
// shows a mid-dispense nozzle drop on a third, and kills + restarts a fourth to prove the
// manager rides out a comms outage (timeout, retry, reconnect). Everything is simulated.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.PumpManager;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: PumpDemo <path-to-pump_host> [basePort]");
    return 2;
}

string pumpHost = args[0];
int basePort = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 9401;
var clock = SystemClock.Instance;

if (!File.Exists(pumpHost))
{
    Console.Error.WriteLine($"firmware host binary not found: {pumpHost}");
    Console.Error.WriteLine("build it first: cmake -S firmware/pump -B firmware/pump/build-host -DHAL=host && cmake --build firmware/pump/build-host");
    return 2;
}

Rule("OpenForecourt — Phase 4 demo (pump firmware ↔ manager over TCP)");
Console.WriteLine("Everything is simulated: four firmware host processes, one pump manager, three");
Console.WriteLine("interchangeable transports (this run uses TCP). Prices in GBP, £1.50/L.\n");

// Pump 2 (index) holsters the nozzle at 3.000 L to demonstrate a customer-initiated stop.
var launchers = new (int port, string[] extra)[]
{
    (basePort + 0, []),
    (basePort + 1, []),
    (basePort + 2, ["--drop-at-ml", "3000"]),
    (basePort + 3, []),
};

var procs = new Process?[4];
for (int i = 0; i < 4; i++)
{
    procs[i] = StartPump(pumpHost, launchers[i].port, launchers[i].extra);
}

await Task.Delay(600); // let the firmware processes bind their listeners

var transports = new TcpPumpTransport[4];
var sessions = new PumpSession[4];
for (int i = 0; i < 4; i++)
{
    int id = i;
    transports[i] = new TcpPumpTransport(new IPEndPoint(IPAddress.Loopback, launchers[i].port), clock);
    sessions[i] = new PumpSession(id, transports[i], clock);
    sessions[i].Observed += (_, n) => Console.WriteLine(Format(n));
}

await using var manager = new PumpManagerService(sessions);
manager.Start();
await WaitConnected(transports, TimeSpan.FromSeconds(5));

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var ct = cts.Token;

// Warmup health-gate: one STATUS round-trip per pump so every link is proven live before the
// timed scenarios (avoids a cold-start race on the very first authorise).
foreach (var s in sessions)
{
    await s.StatusAsync(ct);
}

// ---- Scenario 1: two concurrent fuellings, £20 value preset each ----
Rule("1. Two pumps fuelling concurrently (£20.00 value preset)");
await Task.WhenAll(FuelAsync(sessions[0], ct), FuelAsync(sessions[1], ct));

// ---- Scenario 2: mid-dispense nozzle drop (customer holsters early) ----
Rule("2. Mid-dispense nozzle drop on pump 2 (customer stops at ~3 L)");
await FuelAsync(sessions[2], ct);

// ---- Scenario 3: comms outage — kill the firmware, see timeout + retry, restart, recover ----
Rule("3. Comms timeout and recovery on pump 3");
Console.WriteLine("-> STATUS pump 3 (healthy)");
Console.WriteLine($"   {Describe(await sessions[3].StatusAsync(ct))}");

Console.WriteLine("!! killing pump 3 firmware process");
Kill(ref procs[3]);
await Task.Delay(300);

Console.WriteLine("-> STATUS pump 3 while down (expect timeout after retries)");
var down = await sessions[3].StatusAsync(ct);
Console.WriteLine($"   result: {(down is null ? "TIMEOUT — pump unreachable" : Describe(down))}");

Console.WriteLine("++ restarting pump 3 firmware process");
procs[3] = StartPump(pumpHost, launchers[3].port, launchers[3].extra);
await WaitConnected([transports[3]], TimeSpan.FromSeconds(5));

Console.WriteLine("-> STATUS pump 3 after reconnect");
var recovered = await sessions[3].StatusAsync(ct);
Console.WriteLine($"   {Describe(recovered)}  (link recovered without operator action)");

// ---- Lifetime totals ----
Rule("Lifetime totalizers (non-resettable)");
for (int i = 0; i < 3; i++)
{
    var totals = await sessions[i].TotalsAsync(ct);
    Console.WriteLine($"   pump {i}: {Litres(totals?.Volume ?? Volume.Zero)} L delivered, {Pounds(totals?.Value ?? Money.Zero("GBP"))}");
}

await manager.StopAsync();
for (int i = 0; i < 4; i++)
{
    Kill(ref procs[i]);
}

Rule("Demo complete");
return 0;

// ---- one fuelling: authorise, watch the firmware run to the preset, settle ----
async Task FuelAsync(PumpSession session, CancellationToken token)
{
    Console.WriteLine($"-> pump {session.Id} AUTHORISE value preset £20.00");
    var ack = await session.AuthoriseAsync(PresetMode.Value, 2000, token);
    if (ack.Status != CommandStatus.Ack)
    {
        Console.WriteLine($"   pump {session.Id} authorise {ack.Status}");
        return;
    }

    // Wait until the firmware reports completion (auto-stop at preset, or nozzle drop).
    while (session.State is not DispenserState.Complete && !token.IsCancellationRequested)
    {
        await Task.Delay(50, token);
    }

    Console.WriteLine($"-> pump {session.Id} CANCEL (settle-ack)");
    await session.CancelAsync(token);
}

Process StartPump(string exe, int port, string[] extra)
{
    var psi = new ProcessStartInfo(exe)
    {
        RedirectStandardError = true, // keep the firmware's own log out of the manager trace
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("--port");
    psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
    psi.ArgumentList.Add("--flow-ml-per-tick");
    psi.ArgumentList.Add("600");
    psi.ArgumentList.Add("--tick-ms");
    psi.ArgumentList.Add("60");
    foreach (var a in extra)
    {
        psi.ArgumentList.Add(a);
    }

    var p = Process.Start(psi)!;
    _ = p.StandardError.ReadToEndAsync(); // drain so the pipe never blocks
    return p;
}

static void Kill(ref Process? p)
{
    if (p is null)
    {
        return;
    }

    try
    {
        if (!p.HasExited)
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(2000);
        }
    }
    catch (InvalidOperationException)
    {
        // Already gone.
    }

    p.Dispose();
    p = null;
}

static async Task WaitConnected(IReadOnlyList<TcpPumpTransport> transports, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (transports.All(t => t.State == PumpConnectionState.Connected))
        {
            return;
        }

        await Task.Delay(50);
    }
}

static string Format(PumpNotification n)
{
    string tag = n.ProtocolViolation ? " [PROTOCOL VIOLATION]" : string.Empty;
    return n.Event switch
    {
        PumpEvent.NozzleUp => $"<- pump {n.PumpId} NOZZLE_UP{tag}",
        PumpEvent.NozzleDown => $"<- pump {n.PumpId} NOZZLE_DOWN{tag}",
        PumpEvent.FlowUpdate => $"<- pump {n.PumpId} FLOW {Litres(n.Volume)} L / {Pounds(n.Value)}{tag}",
        PumpEvent.DispenseComplete => $"<- pump {n.PumpId} DISPENSE_COMPLETE {Litres(n.Volume)} L / {Pounds(n.Value)}{tag}",
        PumpEvent.Fault => $"<- pump {n.PumpId} FAULT {n.Fault}{tag}",
        _ => $"<- pump {n.PumpId} 0x{(byte)n.Event:X2}{tag}",
    };
}

static string Describe(PumpStatus? s) =>
    s is null ? "no response" : $"state={s.Value.State} volume={Litres(s.Value.Volume)} L value={Pounds(s.Value.Value)}";

static string Litres(Volume v) => (v.MilliLitres / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

static string Pounds(Money m) => "£" + (m.Minor / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

static void Rule(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 66));
    Console.WriteLine($" {title}");
    Console.WriteLine(new string('=', 66));
}
