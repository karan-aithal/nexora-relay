using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using OpenForecourt.Iso8583;

namespace OpenForecourt.HostSimulator;

/// <summary>
/// The acquiring host's TCP endpoint: accepts connections, reads length-prefixed ISO 8583,
/// asks <see cref="HostEngine"/> what to do, and writes the answer back.
/// </summary>
/// <remarks>
/// <para>
/// One task per accepted connection, all sharing one engine and one ledger — which is the
/// real concurrency story: two pumps authorising at the same instant hit the same ledger,
/// and duplicate detection has to hold under that. The ledger is lock-free
/// (<see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> with a
/// compare-and-swap on reversal) rather than serialised behind a mutex.
/// </para>
/// <para>
/// A protocol error is not recoverable mid-stream: once framing is lost there is no way to
/// find the start of the next message, so the connection is closed. That is what a real
/// host does too.
/// </para>
/// </remarks>
public sealed class HostServer(IPEndPoint endPoint, HostEngine engine, Action<string>? trace = null) : IDisposable
{
    private readonly Iso8583Codec _codec = new();
    private readonly TcpListener _listener = new(endPoint);

    /// <summary>The address actually bound, valid after <see cref="Start"/>. Port 0 becomes a real port.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>The engine answering requests.</summary>
    public HostEngine Engine { get; } = engine;

    /// <summary>Binds and starts listening. Separate from <see cref="AcceptAsync"/> so tests can read the bound port first.</summary>
    public void Start() => _listener.Start();

    /// <summary>Accepts connections until cancelled.</summary>
    public async Task AcceptAsync(CancellationToken cancellationToken)
    {
        Log($"listening on {LocalEndPoint}");
        var connections = new List<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                connections.RemoveAll(t => t.IsCompleted);
                connections.Add(HandleConnectionAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _listener.Stop();
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint;
        Log($"connection from {remote}");

        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var reader = new Iso8583FrameReader(PipeReader.Create(stream));
                var writer = PipeWriter.Create(stream);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                    if (frame.Status == FrameStatus.EndOfStream)
                    {
                        break;
                    }

                    if (frame.Status == FrameStatus.ProtocolError)
                    {
                        Log($"framing error from {remote}: {frame.Error} — closing connection");
                        break;
                    }

                    if (!await HandleFrameAsync(frame.Body, writer, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (IOException ex)
        {
            Log($"connection {remote} dropped: {ex.Message}");
        }
        catch (SocketException ex)
        {
            Log($"connection {remote} socket error: {ex.SocketErrorCode}");
        }

        Log($"connection from {remote} closed");
    }

    /// <summary>Decodes, decides, delays, answers. Returns false when the connection should close.</summary>
    private async Task<bool> HandleFrameAsync(ReadOnlyMemory<byte> body, PipeWriter writer, CancellationToken cancellationToken)
    {
        var decoded = _codec.TryDecode(body.Span);
        if (decoded.IsError)
        {
            // A message we cannot parse cannot be answered — we do not even know its STAN.
            Log($"undecodable message ({decoded.Error}) — closing connection");
            return false;
        }

        var request = decoded.Value;
        Log($"<-- request\n{request}");

        var decision = Engine.Handle(request);
        Log($"    decision: {decision.Note}");

        if (decision.Latency > TimeSpan.Zero)
        {
            await Task.Delay(decision.Latency, cancellationToken).ConfigureAwait(false);
        }

        if (decision.Response is null)
        {
            Log("    (staying silent — the terminal must time out)");
            return true;
        }

        Log($"--> response\n{decision.Response}");
        await Iso8583Framing.WriteFrameAsync(writer, _codec.Encode(decision.Response), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private void Log(string message) => trace?.Invoke($"[host] {message}");
}
