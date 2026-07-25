using System.Threading.Channels;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Adapters.InProc;

/// <summary>
/// An <see cref="IPumpTransport"/> that is a pure in-memory frame pipe: the manager side and a
/// device side face each other through two channels, with no sockets, no wire framing and no
/// CRC. This is the transport CI runs against — deterministic and platform-free — and it
/// deliberately implements the same port the real TCP and serial transports do, so the pump
/// manager cannot tell it is not talking to firmware (CLAUDE.md section 3).
/// </summary>
/// <remarks>
/// Because there is no wire, a <see cref="PumpFrame.Payload"/> here carries the OFP-1 logical
/// body (<c>SEQ | CMD | PAYLOAD</c>) verbatim — the same bytes the TCP transport would wrap in
/// STX/LEN/CRC/ETX. The device side (<see cref="DeviceReceive"/> / <see cref="DeviceSendAsync"/>)
/// is driven by an in-process pump simulator or a test.
/// </remarks>
public sealed class InProcPumpTransport : IPumpTransport
{
    private readonly Channel<PumpFrame> _toDevice =
        Channel.CreateUnbounded<PumpFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<PumpFrame> _toManager =
        Channel.CreateUnbounded<PumpFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <inheritdoc />
    public PumpConnectionState State { get; private set; } = PumpConnectionState.Connected;

    /// <inheritdoc />
    public event EventHandler<PumpConnectionState>? StateChanged;

    /// <inheritdoc />
    public async Task SendFrameAsync(PumpFrame frame, CancellationToken cancellationToken)
    {
        if (State != PumpConnectionState.Connected)
        {
            throw new InvalidOperationException("In-proc pump transport is not connected.");
        }

        await _toDevice.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<PumpFrame> ReceiveFrames(CancellationToken cancellationToken) =>
        _toManager.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Device side: the commands the manager has sent.</summary>
    public IAsyncEnumerable<PumpFrame> DeviceReceive(CancellationToken cancellationToken) =>
        _toDevice.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Device side: send a response or unsolicited event to the manager.</summary>
    public ValueTask DeviceSendAsync(PumpFrame frame, CancellationToken cancellationToken) =>
        _toManager.Writer.WriteAsync(frame, cancellationToken);

    /// <summary>Simulates a link drop and recovery so reconnect handling can be exercised.</summary>
    public void SetConnected(bool connected)
    {
        var next = connected ? PumpConnectionState.Connected : PumpConnectionState.Disconnected;
        if (State == next)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(this, next);
    }
}
