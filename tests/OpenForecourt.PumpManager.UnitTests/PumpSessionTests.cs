using System.Collections.Concurrent;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.PumpManager;
using Xunit;

namespace OpenForecourt.PumpManager.UnitTests;

/// <summary>
/// Session-level behaviour over the in-process transport: a full value-preset fuelling driven by
/// a scripted device (proving command correlation and event parsing end to end), and detection
/// of an illegal transition the firmware reports as a protocol violation via the mirrored FSM.
/// </summary>
public class PumpSessionTests
{
    [Fact]
    public async Task Full_value_preset_fuelling_correlates_commands_and_parses_events()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new InProcPumpTransport();

        // Device emulates firmware: ACK the AUTHORISE, then stream the fuelling events; ACK CANCEL.
        var device = new ScriptedPumpDevice(transport, cmd => (PumpCommand)cmd.Code switch
        {
            PumpCommand.Authorise =>
            [
                ScriptedPumpDevice.Ack(cmd),
                ScriptedPumpDevice.Event(0, PumpEvent.NozzleUp),
                ScriptedPumpDevice.Event(1, PumpEvent.FlowUpdate, ScriptedPumpDevice.VolumeValue(3000, 450)),
                ScriptedPumpDevice.Event(2, PumpEvent.DispenseComplete, ScriptedPumpDevice.VolumeValue(6666, 1000)),
            ],
            _ => [ScriptedPumpDevice.Ack(cmd)],
        });
        var deviceTask = device.RunAsync(cts.Token);

        var session = new PumpSession(3, transport, SystemClock.Instance);
        var completeTcs = new TaskCompletionSource<PumpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Observed += (_, n) =>
        {
            if (n.Event == PumpEvent.DispenseComplete)
            {
                completeTcs.TrySetResult(n);
            }
        };
        var run = session.RunAsync(cts.Token);

        var authAck = await session.AuthoriseAsync(PresetMode.Value, 1000, cts.Token);
        Assert.Equal(CommandStatus.Ack, authAck.Status);

        var complete = await completeTcs.Task.WaitAsync(cts.Token);
        Assert.False(complete.ProtocolViolation);
        Assert.Equal(6666, complete.Volume.MilliLitres);
        Assert.Equal(1000, complete.Value.Minor);
        Assert.Equal(DispenserState.Complete, session.State);

        var cancelAck = await session.CancelAsync(cts.Token);
        Assert.Equal(CommandStatus.Ack, cancelAck.Status);
        Assert.Equal(DispenserState.Idle, session.State);

        await cts.CancelAsync();
        await Swallow(run);
        await Swallow(deviceTask);
    }

    [Fact]
    public async Task Nak_is_surfaced_with_its_error_code()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new InProcPumpTransport();
        var device = new ScriptedPumpDevice(transport, cmd =>
            [new OfpMessage(cmd.Seq, OfpProtocol.Nak, new byte[] { (byte)PumpError.IllegalState })]);
        var deviceTask = device.RunAsync(cts.Token);

        var session = new PumpSession(0, transport, SystemClock.Instance);
        var run = session.RunAsync(cts.Token);

        var result = await session.SuspendAsync(cts.Token);
        Assert.Equal(CommandStatus.Nak, result.Status);
        Assert.Equal(PumpError.IllegalState, result.Error);

        await cts.CancelAsync();
        await Swallow(run);
        await Swallow(deviceTask);
    }

    [Fact]
    public async Task An_illegal_firmware_transition_is_flagged_as_a_protocol_violation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new InProcPumpTransport();
        var device = new ScriptedPumpDevice(transport, cmd => [ScriptedPumpDevice.Ack(cmd)]);
        var deviceTask = device.RunAsync(cts.Token);

        var violations = new ConcurrentQueue<PumpNotification>();
        var session = new PumpSession(0, transport, SystemClock.Instance);
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Observed += (_, n) =>
        {
            if (n.ProtocolViolation)
            {
                violations.Enqueue(n);
                seen.TrySetResult();
            }
        };
        var run = session.RunAsync(cts.Token);

        // NOZZLE_DOWN while Idle is illegal (docs §6.1): the firmware would never send it, so
        // the mirror flags it as a protocol violation rather than trusting it.
        await device.EmitAsync(ScriptedPumpDevice.Event(0, PumpEvent.NozzleDown), cts.Token);

        await seen.Task.WaitAsync(cts.Token);
        Assert.True(violations.TryDequeue(out var v));
        Assert.Equal(PumpEvent.NozzleDown, v.Event);
        Assert.Equal(DispenserState.Idle, session.State); // mirror not advanced on an illegal report

        await cts.CancelAsync();
        await Swallow(run);
        await Swallow(deviceTask);
    }

    private static async Task Swallow(Task task)
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
