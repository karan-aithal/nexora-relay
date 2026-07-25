using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OpenForecourt.SiteController.Realtime;

/// <summary>One broadcast frame: a hub method name and its single argument.</summary>
/// <param name="Method">The client-side method to invoke.</param>
/// <param name="Payload">The argument.</param>
public readonly record struct Frame(string Method, object Payload);

/// <summary>
/// The SignalR backpressure engine: each connection has its own bounded, drop-oldest queue and
/// a dedicated send loop, so a slow dashboard client can never stall the pump pipeline
/// (CLAUDE.md Phase 5, "a slow dashboard client must never block the pump pipeline").
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Broadcast"/> only ever calls <c>TryWrite</c> on bounded channels configured with
/// <see cref="BoundedChannelFullMode.DropOldest"/>, so it returns immediately and never awaits
/// client I/O. A client that cannot keep up simply loses its stalest frames — acceptable for a
/// live view, and far better than back-pressuring the producer. Reconnection resynchronises
/// the client from the authoritative snapshot, so dropped intermediate frames do not matter.
/// </para>
/// <para>
/// It is deliberately free of SignalR types: the actual per-connection send is an injected
/// delegate, which lets the backpressure behaviour be tested with a deliberately slow sender.
/// </para>
/// </remarks>
public sealed class PerConnectionDispatcher(
    Func<string, Frame, CancellationToken, Task> send, ILogger<PerConnectionDispatcher> logger)
{
    private const int Capacity = 32;

    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    /// <summary>Registers a connection and starts its send loop.</summary>
    public void Add(string connectionId)
    {
        var channel = Channel.CreateBounded<Frame>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var cts = new CancellationTokenSource();
        var loop = PumpAsync(connectionId, channel.Reader, cts.Token);
        _connections[connectionId] = new Connection(channel, cts, loop);
    }

    /// <summary>Removes a connection and stops its send loop.</summary>
    public async Task RemoveAsync(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            connection.Channel.Writer.TryComplete();
            await connection.CancelAndWaitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Enqueues a frame to every connection without blocking. Slow clients drop their oldest frames.</summary>
    public void Broadcast(Frame frame)
    {
        foreach (var connection in _connections.Values)
        {
            connection.Channel.Writer.TryWrite(frame); // DropOldest => always succeeds, never blocks
        }
    }

    /// <summary>Number of live connections (for tests/observability).</summary>
    public int ConnectionCount => _connections.Count;

    private async Task PumpAsync(string connectionId, ChannelReader<Frame> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await send(connectionId, frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // connection removed
        }
#pragma warning disable CA1031 // a dead client must not tear anything down; just stop its loop
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogDebug(ex, "Send loop for {ConnectionId} ended.", connectionId);
        }
    }

    private sealed record Connection(Channel<Frame> Channel, CancellationTokenSource Cts, Task Loop)
    {
        public async Task CancelAndWaitAsync()
        {
            await Cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            Cts.Dispose();
        }
    }
}
