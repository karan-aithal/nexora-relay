using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.PumpManager;

/// <summary>Reconnect backoff tuning (<c>docs §8</c>): <c>delay = min(Base·2^attempt, Cap)·(1±Jitter)</c>.</summary>
public sealed record BackoffConfig
{
    /// <summary>First-attempt delay.</summary>
    public TimeSpan Base { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Maximum delay.</summary>
    public TimeSpan Cap { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Fractional jitter applied each attempt (0..1).</summary>
    public double Jitter { get; init; } = 0.2;
}

/// <summary>
/// <see cref="IPumpTransport"/> over TCP to the firmware host build. It owns its reconnection:
/// a background loop connects, reads and re-frames OFP-1 bytes into <see cref="PumpFrame"/>s, and
/// on any drop reconnects with exponential backoff and jitter (<c>docs §8</c>) while
/// <see cref="ReceiveFrames"/> keeps yielding across the gap. The OFP wire framing
/// (STX/LEN/CRC/ETX/stuffing) lives in <see cref="OfpWireCodec"/>, shared with the serial transport.
/// </summary>
public sealed class TcpPumpTransport : IPumpTransport, IAsyncDisposable
{
    private readonly IPEndPoint _endPoint;
    private readonly IClock _clock;
    private readonly BackoffConfig _backoff;
    private readonly Action<string>? _log;
    private readonly Channel<PumpFrame> _rx =
        Channel.CreateUnbounded<PumpFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private volatile NetworkStream? _writeStream;
    private volatile PumpConnectionState _state = PumpConnectionState.Disconnected;
    private int _started;

    /// <summary>Creates a transport targeting <paramref name="endPoint"/> (the firmware host).</summary>
    public TcpPumpTransport(IPEndPoint endPoint, IClock clock, BackoffConfig? backoff = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ArgumentNullException.ThrowIfNull(clock);
        _endPoint = endPoint;
        _clock = clock;
        _backoff = backoff ?? new BackoffConfig();
        _log = log;
    }

    /// <inheritdoc />
    public PumpConnectionState State => _state;

    /// <inheritdoc />
    public event EventHandler<PumpConnectionState>? StateChanged;

    /// <inheritdoc />
    public async Task SendFrameAsync(PumpFrame frame, CancellationToken cancellationToken)
    {
        var stream = _writeStream;
        if (stream is null || _state != PumpConnectionState.Connected)
        {
            // Pump command timing is safety-relevant; stale frames are not queued (IPumpTransport).
            throw new InvalidOperationException("Pump transport is not connected.");
        }

        var wire = OfpWireCodec.EncodeWire(frame.Payload.Span);
        await stream.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PumpFrame> ReceiveFrames(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = ConnectionLoopAsync(cancellationToken);
        }

        await foreach (var frame in _rx.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    SetState(PumpConnectionState.Connecting);
                    client = new TcpClient();
                    await client.ConnectAsync(_endPoint.Address, _endPoint.Port, cancellationToken).ConfigureAwait(false);
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    _writeStream = stream;
                    attempt = 0;
                    SetState(PumpConnectionState.Connected);
                    Log($"connected to {_endPoint}");

                    await PumpReadAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"link error: {ex.GetType().Name} {ex.Message}");
                }
                finally
                {
                    _writeStream = null;
                    client?.Dispose();
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                SetState(PumpConnectionState.Disconnected);
                await DelayBackoffAsync(attempt++, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            SetState(PumpConnectionState.Disconnected);
            _rx.Writer.TryComplete();
        }
    }

    // Reads bytes, re-frames, and publishes frames until the link drops or cancellation.
    private async Task PumpReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var decoder = new PumpFrameDecoder();
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return; // peer closed
            }

            for (int i = 0; i < n; i++)
            {
                if (decoder.Feed(buffer[i]) == OfpDecodeStatus.Ready)
                {
                    // Copy: the decoder reuses its buffer on the next Feed.
                    _rx.Writer.TryWrite(new PumpFrame(decoder.Body.ToArray()));
                }
            }
        }
    }

    private async Task DelayBackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        double capMs = _backoff.Cap.TotalMilliseconds;
        double baseMs = Math.Min(_backoff.Base.TotalMilliseconds * Math.Pow(2, attempt), capMs);
        double jitter = 1.0 + ((Random.Shared.NextDouble() * 2 - 1) * _backoff.Jitter);
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(baseMs * jitter, 0, capMs));
        await _clock.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private void SetState(PumpConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void Log(string message) => _log?.Invoke($"[pump-tcp] {message}");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeStream = null;
        _rx.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
