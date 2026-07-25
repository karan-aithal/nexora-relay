using System.IO.Ports;
using System.Threading.Channels;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.PumpManager;

namespace OpenForecourt.Adapters.Serial;

/// <summary>
/// <see cref="IPumpTransport"/> over a real serial port — a <c>com0com</c> virtual null-modem
/// pair in development, a physical RS-232/RS-485 dispenser link in the field. Windows-only
/// (excluded from the Linux CI solution filter), and deliberately the <b>same</b> port the TCP
/// and in-process transports implement: swapping a TCP firmware link for a serial one is a
/// configuration change, not a code change (CLAUDE.md section 3).
/// </summary>
/// <remarks>
/// The OFP-1 wire framing (STX/LEN/CRC/ETX/stuffing) is reused verbatim from
/// <see cref="OfpWireCodec"/>, so the exact bytes on the serial line match those on the socket
/// and the firmware cannot tell the two apart. Reconnection reopens the port with the same
/// exponential-backoff policy as the TCP transport (<c>docs §8</c>).
/// </remarks>
public sealed class SerialPumpTransport : IPumpTransport, IAsyncDisposable
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly IClock _clock;
    private readonly BackoffConfig _backoff;
    private readonly Action<string>? _log;
    private readonly Channel<PumpFrame> _rx =
        Channel.CreateUnbounded<PumpFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private volatile SerialPort? _port;
    private volatile PumpConnectionState _state = PumpConnectionState.Disconnected;
    private int _started;

    /// <summary>Creates a transport bound to <paramref name="portName"/> (e.g. "COM3").</summary>
    public SerialPumpTransport(string portName, int baudRate, IClock clock, BackoffConfig? backoff = null,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(portName);
        ArgumentNullException.ThrowIfNull(clock);
        _portName = portName;
        _baudRate = baudRate;
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
        var port = _port;
        if (port is null || _state != PumpConnectionState.Connected)
        {
            throw new InvalidOperationException("Serial pump transport is not connected.");
        }

        var wire = OfpWireCodec.EncodeWire(frame.Payload.Span);
        await port.BaseStream.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                SerialPort? port = null;
                try
                {
                    SetState(PumpConnectionState.Connecting);
                    port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One);
                    port.Open();
                    _port = port;
                    attempt = 0;
                    SetState(PumpConnectionState.Connected);
                    Log($"opened {_portName} @ {_baudRate}");

                    await PumpReadAsync(port, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"serial error: {ex.GetType().Name} {ex.Message}");
                }
                finally
                {
                    _port = null;
                    port?.Dispose();
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

    private async Task PumpReadAsync(SerialPort port, CancellationToken cancellationToken)
    {
        var decoder = new PumpFrameDecoder();
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            int n = await port.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return;
            }

            for (int i = 0; i < n; i++)
            {
                if (decoder.Feed(buffer[i]) == OfpDecodeStatus.Ready)
                {
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

    private void Log(string message) => _log?.Invoke($"[pump-serial] {message}");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _port = null;
        _rx.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
