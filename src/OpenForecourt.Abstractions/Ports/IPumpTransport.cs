namespace OpenForecourt.Abstractions.Ports;

/// <summary>The connection state of a pump transport.</summary>
public enum PumpConnectionState
{
    /// <summary>Not connected; no frames can be sent or received.</summary>
    Disconnected,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>Connected; frames flow in both directions.</summary>
    Connected,
}

/// <summary>
/// A single framed message to or from the pump firmware. The byte layout is defined by
/// <c>docs/protocol-pump.md</c> (written in Phase 4, before any pump code); Phase 0 only
/// needs the opaque envelope.
/// </summary>
/// <param name="Payload">The raw frame payload, excluding transport framing.</param>
public readonly record struct PumpFrame(ReadOnlyMemory<byte> Payload);

/// <summary>
/// The link to a single pump controller. Implementations include a serial link over a
/// com0com virtual null-modem (Windows), a TCP link to the firmware host build, and an
/// in-memory frame pipe (for CI).
/// </summary>
/// <remarks>
/// <para>
/// Reconnect semantics: the transport is responsible for its own reconnection. When the
/// underlying link drops, <see cref="State"/> transitions to
/// <see cref="PumpConnectionState.Disconnected"/> then
/// <see cref="PumpConnectionState.Connecting"/>; <see cref="ReceiveFrames"/> does not
/// complete on a transient drop — it resumes yielding frames once reconnected. It
/// completes only when the transport is disposed or cancellation is requested.
/// </para>
/// <para>
/// Frames sent via <see cref="SendFrameAsync"/> while disconnected are not queued;
/// the returned task faults or the caller is expected to gate on <see cref="State"/>.
/// This is intentional — pump command timing is safety-relevant and stale queued frames
/// must not be delivered late.
/// </para>
/// </remarks>
public interface IPumpTransport
{
    /// <summary>The current connection state.</summary>
    PumpConnectionState State { get; }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<PumpConnectionState> StateChanged;

    /// <summary>Sends one frame to the pump.</summary>
    /// <param name="frame">The frame to send.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task SendFrameAsync(PumpFrame frame, CancellationToken cancellationToken);

    /// <summary>
    /// Yields frames received from the pump as they arrive. Survives transient reconnects;
    /// completes on disposal or cancellation.
    /// </summary>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    IAsyncEnumerable<PumpFrame> ReceiveFrames(CancellationToken cancellationToken);
}
