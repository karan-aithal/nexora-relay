using OpenForecourt.Adapters.InProc;
using OpenForecourt.PumpManager;
using Xunit;

namespace OpenForecourt.PumpManager.UnitTests;

/// <summary>
/// Eight pumps running at once, each over its own in-process link to its own device, hammered
/// with concurrent commands — asserting no cross-talk (every session only ever sees its own
/// pump's marker) and clean shutdown (every loop finishes with no orphaned tasks). This is the
/// concurrency deliverable of Phase 4.
/// </summary>
public class PumpManagerConcurrencyTests
{
    [Fact]
    public async Task Eight_pumps_under_load_have_no_cross_talk_and_shut_down_cleanly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var devices = new List<ScriptedPumpDevice>();
        var deviceTasks = new List<Task>();
        var sessions = new List<PumpSession>();

        for (int i = 0; i < PumpManagerService.MaxPumps; i++)
        {
            uint marker = (uint)(i + 1); // unique per pump, carried in the STATUS volume field
            var transport = new InProcPumpTransport();
            var device = new ScriptedPumpDevice(transport, cmd =>
                cmd.Code == (byte)PumpCommand.StatusReq
                    ? [ScriptedPumpDevice.Ack(cmd, ScriptedPumpDevice.StatusPayload(DispenserState.Idle, marker))]
                    : [ScriptedPumpDevice.Ack(cmd)]);

            devices.Add(device);
            deviceTasks.Add(device.RunAsync(cts.Token));
            sessions.Add(new PumpSession(i, transport, SystemClock.Instance));
        }

        await using var manager = new PumpManagerService(sessions);
        manager.Start();

        // Load: each pump handles 100 concurrent status reads; each must return only its marker.
        const int perPump = 100;
        var work = sessions.Select(async session =>
        {
            var reads = Enumerable.Range(0, perPump).Select(_ => session.StatusAsync(cts.Token));
            var results = await Task.WhenAll(reads);
            foreach (var status in results)
            {
                Assert.NotNull(status);
                Assert.Equal(session.Id + 1, status!.Value.Volume.MilliLitres); // its own marker, never a neighbour's
            }
        });

        await Task.WhenAll(work);

        // Clean shutdown: StopAsync completes, which means every RunAsync loop finished.
        await manager.StopAsync();

        // Devices stop when the outer token cancels.
        await cts.CancelAsync();
        await Task.WhenAll(deviceTasks);

        Assert.All(devices, d => Assert.Equal(perPump, d.CommandsReceived));
    }

    [Fact]
    public async Task Stop_completes_promptly_with_no_orphaned_tasks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new InProcPumpTransport();
        var device = new ScriptedPumpDevice(transport, cmd => [ScriptedPumpDevice.Ack(cmd)]);
        var deviceTask = device.RunAsync(cts.Token);

        var manager = new PumpManagerService([new PumpSession(0, transport, SystemClock.Instance)]);
        manager.Start();
        await manager.Sessions[0].CancelAsync(cts.Token); // one exchange to prove it is live

        var stop = manager.StopAsync();
        var completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
        Assert.Same(stop, completed); // shutdown did not hang
        await stop;

        await cts.CancelAsync();
        await deviceTask;
    }
}
