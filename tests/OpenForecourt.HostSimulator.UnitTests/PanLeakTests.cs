using System.Collections.Concurrent;
using System.Net;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.HostSimulator;
using OpenForecourt.Iso8583;
using Xunit;

namespace OpenForecourt.HostSimulator.UnitTests;

/// <summary>
/// CLAUDE.md section 7: PAN masking is enforced by a test, not by discipline. This runs a
/// full transaction through the host and terminal, captures <b>every</b> trace sink on both
/// sides plus the rules echo, and fails if any cleartext test PAN appears in it.
/// </summary>
/// <remarks>
/// It scans for the exact PAN values used, not for a generic long-digit-run: legitimate
/// numeric fields — field 4 (amount, 12 digits) and field 90 (original data elements, 42
/// digits) — are digit runs that are not PANs, so a shape-based scan would false-positive.
/// The cleartext PAN is what must never leak, so the cleartext PAN is what is asserted.
/// </remarks>
public sealed class PanLeakTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private static readonly string[] ClearPans = ["4111111111111111", "5555555555554444"];

    [Fact]
    public async Task No_trace_sink_ever_sees_an_unmasked_pan()
    {
        var lines = new ConcurrentQueue<string>();
        void Capture(string line) => lines.Enqueue(line);

        var rules = new HostRules
        {
            ApproveBelowMinor = 10_000,
            DeclineByPan = new Dictionary<string, string> { ["5555555555554444"] = HostResponseCodes.ExpiredCard },
        };
        Capture(rules.Describe());

        var engine = new HostEngine(rules, new HostLedger(), SystemClock.Instance);
        using var server = new HostServer(new IPEndPoint(IPAddress.Loopback, 0), engine, Capture);
        server.Start();
        var serving = server.AcceptAsync(_cts.Token);

        await using (var terminal = new HostClient(Capture))
        {
            await terminal.ConnectAsync(server.LocalEndPoint, _cts.Token);
            var now = DateTimeOffset.UtcNow;
            var timeout = TimeSpan.FromSeconds(5);

            var approved = TerminalMessages.Financial("4111111111111111", 4500, "000001", now);
            await terminal.SendAsync(approved, timeout, _cts.Token);
            await terminal.SendAsync(TerminalMessages.Financial("5555555555554444", 1000, "000002", now), timeout, _cts.Token);
            await terminal.SendAsync(TerminalMessages.Reversal(approved, "000003", now), timeout, _cts.Token);
        }

        await _cts.CancelAsync();
        try
        {
            await serving;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotEmpty(lines);
        // Sanity: the masked forms must actually be in the trace, so this test is exercising
        // real PAN-bearing output and not silently passing on empty logs.
        Assert.Contains(lines, l => l.Contains("411111******1111", StringComparison.Ordinal));

        foreach (string line in lines)
        {
            foreach (string pan in ClearPans)
            {
                Assert.DoesNotContain(pan, line, StringComparison.Ordinal);
            }
        }
    }

    public void Dispose() => _cts.Dispose();
}
