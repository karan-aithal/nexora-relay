using System.Globalization;
using System.IO.Pipelines;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Iso8583;
using OpenForecourt.SiteController.Config;

namespace OpenForecourt.SiteController.Hosting;

/// <summary>
/// The production host link: length-prefixed ISO 8583 over TCP to the acquiring host
/// (CLAUDE.md section 3), with a timeout that yields <see cref="AuthorisationOutcome.NoResponse"/>.
/// </summary>
/// <remarks>
/// <para>
/// Exchanges are serialised behind a semaphore — a forecourt's authorisation rate does not
/// need request multiplexing, and serial request/response keeps STAN correlation trivial. The
/// connection is opened lazily and re-opened after any fault, so a host simulator that is
/// killed and restarted mid-demo is transparently reconnected.
/// </para>
/// <para>
/// The read is raced against <see cref="IClock.Delay"/>, so the host timeout is the one
/// configured value and is driven by the same clock as the rest of the system.
/// </para>
/// <para>
/// The host name is resolved lazily, per connect, so the site controller starts and runs
/// (in offline mode) even when the host is down or its DNS name does not yet resolve — which
/// is exactly the state a restart-during-outage lands in.
/// </para>
/// </remarks>
public sealed class TcpHostConnection(string host, int port, IClock clock, SiteOptions options, ILogger<TcpHostConnection> logger)
    : IHostConnection, IHostProbe, IAsyncDisposable
{
    private readonly Iso8583Codec _codec = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _stan;
    private TcpClient? _client;
    private Iso8583FrameReader? _reader;
    private PipeWriter? _writer;

    /// <inheritdoc />
    public async Task<HostResponse> SendAsync(FinancialRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var message = BuildFinancial(request);
            await Iso8583Framing.WriteFrameAsync(_writer!, _codec.Encode(message), cancellationToken).ConfigureAwait(false);

            var readTask = _reader!.ReadFrameAsync(cancellationToken).AsTask();
            var timeout = clock.Delay(options.HostTimeout, cancellationToken);
            if (await Task.WhenAny(readTask, timeout).ConfigureAwait(false) != readTask)
            {
                logger.LogWarning("Host timeout after {Timeout} for {TransactionId}.", options.HostTimeout, request.TransactionId);
                Reset(); // a late reply must not be mistaken for this request's response
                return new HostResponse(AuthorisationOutcome.NoResponse, null);
            }

            var frame = await readTask.ConfigureAwait(false);
            if (frame.Status != FrameStatus.Frame)
            {
                Reset();
                return new HostResponse(AuthorisationOutcome.NoResponse, null);
            }

            var decoded = _codec.TryDecode(frame.Body.Span);
            if (decoded.IsError)
            {
                return new HostResponse(AuthorisationOutcome.NoResponse, null);
            }

            var response = decoded.Value;
            string? code = response[Fields.ResponseCode];
            return code == "00"
                ? new HostResponse(AuthorisationOutcome.Approved, response[Fields.AuthorisationCode] ?? "000000")
                : new HostResponse(AuthorisationOutcome.Declined, null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Host link fault for {TransactionId}.", request.TransactionId);
            Reset();
            return new HostResponse(AuthorisationOutcome.NoResponse, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            Reset();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { Connected: true })
        {
            return;
        }

        Reset();
        var client = new TcpClient();
        // ConnectAsync(host, port) resolves the name each attempt, so a host that appears later works.
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        _client = client;
        _reader = new Iso8583FrameReader(PipeReader.Create(stream));
        _writer = PipeWriter.Create(stream);
        logger.LogInformation("Host link connected to {Host}:{Port}.", host, port);
    }

    private void Reset()
    {
        _client?.Dispose();
        _client = null;
        _reader = null;
        _writer = null;
    }

    private Iso8583Message BuildFinancial(FinancialRequest request)
    {
        var now = clock.UtcNow;
        string stan = (Interlocked.Increment(ref _stan) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        return Iso8583Message.Create("0200")
            .Set(Fields.Pan, options.TestPan)
            .Set(Fields.ProcessingCode, "000000")
            .Set(Fields.Amount, request.Amount.Minor)
            .Set(Fields.TransmissionDateTime, now.ToString("MMddHHmmss", CultureInfo.InvariantCulture))
            .Set(Fields.Stan, stan)
            .Set(Fields.LocalTime, now.ToString("HHmmss", CultureInfo.InvariantCulture))
            .Set(Fields.LocalDate, now.ToString("MMdd", CultureInfo.InvariantCulture))
            .Set(Fields.PosEntryMode, "051")
            .Set(Fields.TerminalId, "OPT00001")
            .Set(Fields.MerchantId, "OPENFORECOURT01")
            .Set(Fields.Currency, "826")
            .Build();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Reset();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
