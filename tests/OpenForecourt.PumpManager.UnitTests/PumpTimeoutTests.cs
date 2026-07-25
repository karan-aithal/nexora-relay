using OpenForecourt.Adapters.InProc;
using OpenForecourt.PumpManager;
using Xunit;

namespace OpenForecourt.PumpManager.UnitTests;

/// <summary>
/// Deterministic-time timeout/retry tests using a fake clock that advances on each delay
/// (Phase 4). No real waiting: a silent pump produces exactly <c>1 + MaxRetries</c> transmissions
/// and then the command fails as a timeout (<c>docs §4.2</c>).
/// </summary>
public class PumpTimeoutTests
{
    [Fact]
    public async Task A_silent_pump_causes_exactly_maxretries_retransmissions_then_timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = new AutoAdvanceClock(DateTimeOffset.UnixEpoch);
        var transport = new InProcPumpTransport();
        var device = new ScriptedPumpDevice(transport, _ => []); // never answers
        var deviceTask = device.RunAsync(cts.Token);

        var config = new PumpSessionConfig { ResponseTimeout = TimeSpan.FromMilliseconds(1000), MaxRetries = 3 };
        var session = new PumpSession(0, transport, clock, config);
        var run = session.RunAsync(cts.Token);

        var result = await session.CancelAsync(cts.Token);

        Assert.Equal(CommandStatus.Timeout, result.Status);
        await Wait.ForAsync(() => device.CommandsReceived >= 4, cts.Token);
        Assert.Equal(4, device.CommandsReceived); // 1 initial + 3 retransmissions

        await cts.CancelAsync();
        await SwallowCancel(run);
        await SwallowCancel(deviceTask);
    }

    [Fact]
    public async Task Timeout_is_reported_without_advancing_the_dispenser_state()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = new AutoAdvanceClock(DateTimeOffset.UnixEpoch);
        var transport = new InProcPumpTransport();
        var device = new ScriptedPumpDevice(transport, _ => []);
        var deviceTask = device.RunAsync(cts.Token);

        var session = new PumpSession(0, transport, clock,
            new PumpSessionConfig { ResponseTimeout = TimeSpan.FromMilliseconds(500), MaxRetries = 2 });
        var run = session.RunAsync(cts.Token);

        var result = await session.AuthoriseAsync(PresetMode.Value, 1000, cts.Token);

        Assert.Equal(CommandStatus.Timeout, result.Status);
        Assert.Equal(DispenserState.Idle, session.State); // no ACK -> mirror never left Idle

        await cts.CancelAsync();
        await SwallowCancel(run);
        await SwallowCancel(deviceTask);
    }

    private static async Task SwallowCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
