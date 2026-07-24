using System.Globalization;
using System.Net;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.HostSimulator;
using OpenForecourt.Iso8583;

var command = args.Length > 0 ? args[0] : "demo";
var rulesPath = ArgValue("--rules") ?? DefaultRulesPath();
var rules = File.Exists(rulesPath) ? HostRules.Load(rulesPath) : new HostRules();
var port = int.Parse(ArgValue("--port") ?? "9583", CultureInfo.InvariantCulture);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

switch (command)
{
    case "serve":
        await ServeAsync(new IPEndPoint(IPAddress.Loopback, port), rules, stopping.Token);
        return 0;

    case "demo":
        return await DemoAsync(rules, stopping.Token);

    default:
        Console.Error.WriteLine("usage: HostSimulator [serve|demo] [--port N] [--rules path]");
        return 2;
}

static string? ArgValue(string name)
{
    var argv = Environment.GetCommandLineArgs();
    int i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

static string DefaultRulesPath() => Path.Combine(AppContext.BaseDirectory, "hostrules.json");

static async Task ServeAsync(IPEndPoint endPoint, HostRules rules, CancellationToken cancellationToken)
{
    var engine = new HostEngine(rules, new HostLedger(), SystemClock.Instance);
    using var server = new HostServer(endPoint, engine, Console.WriteLine);
    server.Start();
    Console.WriteLine($"rules: {rules.Describe()}");
    await server.AcceptAsync(cancellationToken);
}

static async Task<int> DemoAsync(HostRules rules, CancellationToken cancellationToken)
{
    var clock = SystemClock.Instance;
    var ledger = new HostLedger();
    var engine = new HostEngine(rules, ledger, clock);

    // Port 0: the OS picks a free port, so the demo never collides with anything.
    using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    using var server = new HostServer(new IPEndPoint(IPAddress.Loopback, 0), engine, Console.WriteLine);
    server.Start();
    var serving = server.AcceptAsync(shutdown.Token);

    Console.WriteLine($"rules: {rules.Describe()}");
    Console.WriteLine();

    await using var terminal = new HostClient(Console.WriteLine);
    await terminal.ConnectAsync(server.LocalEndPoint, cancellationToken);

    var now = clock.UtcNow;
    var timeout = TimeSpan.FromSeconds(3);

    Step("1. Echo test — is the acquirer link alive?");
    await terminal.SendAsync(TerminalMessages.NetworkManagement("301", "000001", now), timeout, cancellationToken);

    Step("2. Approval — £45.00, below the approval threshold");
    var approved = TerminalMessages.Financial("4111111111111111", 4500, "000002", now);
    var approval = await terminal.SendAsync(approved, timeout, cancellationToken);
    Console.WriteLine($"    ledger: {ledger.Summary()}");

    Step("3. Duplicate — same terminal, same STAN, resent");
    await terminal.SendAsync(approved, timeout, cancellationToken);

    Step("4. Decline by PAN — the configured expired-card test PAN");
    await terminal.SendAsync(TerminalMessages.Financial("5555555555554444", 1000, "000003", now), timeout, cancellationToken);

    Step("5. Decline by amount — £250.00, at or above the threshold");
    await terminal.SendAsync(TerminalMessages.Financial("4111111111111111", 25000, "000004", now), timeout, cancellationToken);

    Step("6. Timeout — the host is configured to stay silent for this PAN");
    var unanswered = TerminalMessages.Financial("378282246310005", 3000, "000005", now);
    var timedOut = await terminal.SendAsync(unanswered, timeout, cancellationToken);
    Console.WriteLine($"    terminal outcome: {timedOut.Status} — the transaction's state at the host is UNKNOWN");

    Step("7. Reversal — undo the approval from step 2");
    Console.WriteLine($"    ledger before: {ledger.Summary()}");
    await terminal.SendAsync(TerminalMessages.Reversal(approved, "000006", clock.UtcNow), timeout, cancellationToken);
    Console.WriteLine($"    ledger after:  {ledger.Summary()}");

    Console.WriteLine();
    Console.WriteLine("Ledger:");
    foreach (var entry in ledger.Entries.OrderBy(e => e.Stan, StringComparer.Ordinal))
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  terminal {entry.TerminalId} STAN {entry.Stan}  {entry.Mti}  {entry.AmountMinor,8} {entry.CurrencyCode}  rc {entry.ResponseCode}  auth {entry.AuthCode ?? "-"}  {(entry.Reversed ? "REVERSED" : string.Empty)}"));
    }

    await terminal.DisposeAsync();
    await shutdown.CancelAsync();
    await serving;

    Console.WriteLine();
    Console.WriteLine("demo complete.");
    return approval.Response?[Fields.ResponseCode] == HostResponseCodes.Approved ? 0 : 1;

    static void Step(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 78));
        Console.WriteLine(title);
        Console.WriteLine(new string('-', 78));
    }
}
