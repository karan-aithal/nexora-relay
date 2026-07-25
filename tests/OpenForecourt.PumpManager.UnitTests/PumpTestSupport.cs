using System.Buffers.Binary;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.PumpManager;

namespace OpenForecourt.PumpManager.UnitTests;

/// <summary>
/// A fake clock that advances its own time by the requested delay on every <see cref="Delay"/>,
/// so timeout/retry logic runs to completion deterministically with no real waiting. One delay
/// call == one elapsed interval, which makes retransmission counts exact.
/// </summary>
internal sealed class AutoAdvanceClock(DateTimeOffset start) : IClock
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_gate)
        {
            if (delay > TimeSpan.Zero)
            {
                _now += delay;
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// A scriptable in-process pump device driving the device side of an
/// <see cref="InProcPumpTransport"/>. Each received command is passed to a handler that returns
/// the frames to send back (an ACK/NAK, and any unsolicited events). It can also emit events
/// unprompted. Used to stand in for firmware in deterministic tests.
/// </summary>
internal sealed class ScriptedPumpDevice(InProcPumpTransport transport, Func<OfpMessage, IEnumerable<OfpMessage>> handler)
{
    private int _commandsReceived;

    public int CommandsReceived => Volatile.Read(ref _commandsReceived);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in transport.DeviceReceive(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _commandsReceived);
                var command = OfpMessage.Parse(frame.Payload);
                foreach (var reply in handler(command))
                {
                    await transport.DeviceSendAsync(new PumpFrame(reply.ToBody()), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public ValueTask EmitAsync(OfpMessage message, CancellationToken cancellationToken) =>
        transport.DeviceSendAsync(new PumpFrame(message.ToBody()), cancellationToken);

    /// <summary>Builds an ACK for a command, echoing its SEQ and setting the ACK bit.</summary>
    public static OfpMessage Ack(OfpMessage command, byte[]? payload = null) =>
        new(command.Seq, (byte)(command.Code | OfpProtocol.AckMask), payload ?? []);

    /// <summary>A STATUS ACK payload with a marker written into the volume field.</summary>
    public static byte[] StatusPayload(DispenserState state, uint markerVolume, uint value = 0)
    {
        var payload = new byte[9];
        payload[0] = (byte)state;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), markerVolume);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), value);
        return payload;
    }

    /// <summary>An unsolicited event frame with the given event SEQ.</summary>
    public static OfpMessage Event(byte eventSeq, PumpEvent code, byte[]? payload = null) =>
        new(eventSeq, (byte)code, payload ?? []);

    /// <summary>A FLOW_UPDATE / DISPENSE_COMPLETE payload (volume mL, value minor units).</summary>
    public static byte[] VolumeValue(uint volumeMl, uint value)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload, volumeMl);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), value);
        return payload;
    }
}

/// <summary>Small test helpers.</summary>
internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until true or the token cancels.</summary>
    public static async Task ForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }
}
