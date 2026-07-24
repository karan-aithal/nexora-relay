using System.Net;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.HostSimulator;
using OpenForecourt.Iso8583;
using Xunit;

namespace OpenForecourt.HostSimulator.UnitTests;

/// <summary>
/// End-to-end over a real loopback TCP socket: terminal client to host server, through the
/// framing and the codec. This is where the concurrency, the framing and the decision logic
/// meet, so it is where a wiring bug surfaces that the unit tests miss.
/// </summary>
public sealed class HostServerTests : IAsyncLifetime, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private HostServer _server = null!;
    private HostLedger _ledger = null!;
    private Task _serving = Task.CompletedTask;

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task<HostClient> StartAsync(HostRules rules)
    {
        _ledger = new HostLedger();
        var engine = new HostEngine(rules, _ledger, SystemClock.Instance);
        _server = new HostServer(new IPEndPoint(IPAddress.Loopback, 0), engine);
        _server.Start();
        _serving = _server.AcceptAsync(_cts.Token);

        var client = new HostClient();
        await client.ConnectAsync(_server.LocalEndPoint, _cts.Token);
        return client;
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
        }

    }

    public void Dispose()
    {
        _server?.Dispose();
        _cts.Dispose();
    }

    [Fact]
    public async Task An_approval_round_trips_over_tcp()
    {
        await using var client = await StartAsync(new HostRules { ApproveBelowMinor = 10_000 });

        var exchange = await client.SendAsync(
            TerminalMessages.Financial("4111111111111111", 4500, "000001", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(5),
            _cts.Token);

        Assert.Equal(ExchangeStatus.Response, exchange.Status);
        Assert.Equal(HostResponseCodes.Approved, exchange.Response![Fields.ResponseCode]);
    }

    [Fact]
    public async Task A_silent_pan_makes_the_client_time_out()
    {
        await using var client = await StartAsync(new HostRules { NoResponsePans = ["378282246310005"] });

        var exchange = await client.SendAsync(
            TerminalMessages.Financial("378282246310005", 3000, "000001", DateTimeOffset.UtcNow),
            TimeSpan.FromMilliseconds(300),
            _cts.Token);

        Assert.Equal(ExchangeStatus.Timeout, exchange.Status);
    }

    [Fact]
    public async Task Injected_latency_below_the_timeout_still_returns_a_response()
    {
        await using var client = await StartAsync(new HostRules { LatencyMs = 200, ApproveBelowMinor = 10_000 });

        var exchange = await client.SendAsync(
            TerminalMessages.Financial("4111111111111111", 4500, "000001", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(5),
            _cts.Token);

        Assert.Equal(ExchangeStatus.Response, exchange.Status);
    }

    [Fact]
    public async Task A_reversal_over_tcp_reverses_the_ledger()
    {
        await using var client = await StartAsync(new HostRules { ApproveBelowMinor = 10_000 });
        var now = DateTimeOffset.UtcNow;
        var original = TerminalMessages.Financial("4111111111111111", 4500, "000001", now);

        await client.SendAsync(original, TimeSpan.FromSeconds(5), _cts.Token);
        Assert.Equal(4500, _ledger.OutstandingMinor);

        var reversal = await client.SendAsync(
            TerminalMessages.Reversal(original, "000002", now), TimeSpan.FromSeconds(5), _cts.Token);

        Assert.Equal(HostResponseCodes.Approved, reversal.Response![Fields.ResponseCode]);
        Assert.Equal(0, _ledger.OutstandingMinor);
    }

    [Fact]
    public async Task Many_concurrent_terminals_never_double_authorise_a_shared_stan()
    {
        await using var setup = await StartAsync(new HostRules { ApproveBelowMinor = 1_000_000 });

        // Twenty clients race to authorise the SAME terminal id + STAN. Exactly one may be
        // approved; the rest must see 94 duplicate. This is the real concurrency claim.
        const int terminals = 20;
        var now = DateTimeOffset.UtcNow;
        var request = TerminalMessages.Financial("4111111111111111", 4500, "555555", now);

        var exchanges = await Task.WhenAll(Enumerable.Range(0, terminals).Select(async _ =>
        {
            await using var client = new HostClient();
            await client.ConnectAsync(_server.LocalEndPoint, _cts.Token);
            return await client.SendAsync(request, TimeSpan.FromSeconds(10), _cts.Token);
        }));

        int approved = exchanges.Count(e => e.Response?[Fields.ResponseCode] == HostResponseCodes.Approved);
        int duplicate = exchanges.Count(e => e.Response?[Fields.ResponseCode] == HostResponseCodes.DuplicateTransmission);

        Assert.Equal(1, approved);
        Assert.Equal(terminals - 1, duplicate);
    }
}
