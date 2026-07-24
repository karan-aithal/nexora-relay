using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using OpenForecourt.Iso8583;

namespace OpenForecourt.HostSimulator;

/// <summary>How a request to the host ended.</summary>
public enum ExchangeStatus
{
    /// <summary>A response was received and decoded.</summary>
    Response,

    /// <summary>No response arrived within the timeout. The transaction's state at the host is unknown.</summary>
    Timeout,

    /// <summary>The link or the response was unusable.</summary>
    Error,
}

/// <summary>The outcome of one request/response exchange.</summary>
/// <param name="Status">Response, timeout or error.</param>
/// <param name="Response">The decoded response when <see cref="ExchangeStatus.Response"/>.</param>
/// <param name="Error">The diagnostic when <see cref="ExchangeStatus.Error"/>.</param>
public readonly record struct Exchange(ExchangeStatus Status, Iso8583Message? Response, string? Error);

/// <summary>
/// A diagnostic terminal-side client for the host simulator: connect, send one message,
/// wait for the matching response or time out.
/// </summary>
/// <remarks>
/// This is the tool used by the phase demo and the end-to-end tests. It is deliberately
/// request/response serial — the production <c>IHostConnection</c> adapter, which
/// multiplexes and correlates on STAN, arrives with the OPT in a later phase.
/// </remarks>
public sealed class HostClient : IAsyncDisposable
{
    private readonly Iso8583Codec _codec = new();
    private readonly TcpClient _client = new();
    private readonly Action<string>? _trace;
    private Iso8583FrameReader? _reader;
    private PipeWriter? _writer;

    /// <summary>Creates a client. Nothing connects until <see cref="ConnectAsync"/>.</summary>
    public HostClient(Action<string>? trace = null) => _trace = trace;

    /// <summary>Connects to the host.</summary>
    public async Task ConnectAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        await _client.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
        var stream = _client.GetStream();
        _reader = new Iso8583FrameReader(PipeReader.Create(stream));
        _writer = PipeWriter.Create(stream);
        Log($"connected to {endPoint}");
    }

    /// <summary>
    /// Sends a request and waits up to <paramref name="timeout"/> for the response.
    /// </summary>
    /// <remarks>
    /// A timeout is a <b>return value</b>, not an exception: at the terminal it is an
    /// expected outcome with a defined consequence (reverse the transaction), not a fault.
    /// </remarks>
    public async Task<Exchange> SendAsync(Iso8583Message request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_reader is null || _writer is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        Log($"--> request\n{request}");
        await Iso8583Framing.WriteFrameAsync(_writer, _codec.Encode(request), cancellationToken).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        FrameResult frame;
        try
        {
            frame = await _reader.ReadFrameAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"    no response within {timeout.TotalSeconds:0.0}s — TIMEOUT");
            return new Exchange(ExchangeStatus.Timeout, null, null);
        }

        if (frame.Status != FrameStatus.Frame)
        {
            return new Exchange(ExchangeStatus.Error, null, frame.Error ?? frame.Status.ToString());
        }

        var decoded = _codec.TryDecode(frame.Body.Span);
        if (decoded.IsError)
        {
            return new Exchange(ExchangeStatus.Error, null, decoded.Error.ToString());
        }

        Log($"<-- response\n{decoded.Value}");
        return new Exchange(ExchangeStatus.Response, decoded.Value, null);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Log(string message) => _trace?.Invoke($"[terminal] {message}");
}
