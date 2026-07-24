using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using OpenForecourt.Abstractions;
using OpenForecourt.Abstractions.Ports;
using static OpenForecourt.Adapters.Pcsc.WinScard;

namespace OpenForecourt.Adapters.Pcsc;

/// <summary>
/// The real production card reader: <see cref="ICardReader"/> over the Windows PC/SC API by
/// direct <c>winscard.dll</c> P/Invoke. Swapping a simulated card reader for a physical
/// ACR122U is choosing this implementation in configuration — it is not a code change
/// (CLAUDE.md section 3). It requires Windows and a PC/SC service; it is excluded from the
/// Linux CI build for that reason.
/// </summary>
/// <remarks>
/// The blocking PC/SC calls run on the thread pool via <see cref="Task.Run(Action)"/> so the
/// async port contract holds without an <c>async void</c> or a <c>.Result</c> anywhere
/// (CLAUDE.md section 6). Cancellation is honoured by polling <c>SCardGetStatusChange</c>
/// with a short per-iteration timeout rather than blocking indefinitely.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PcscCardReader : ICardReader, IDisposable
{
    private readonly string? _preferredReader;
    private nint _context;
    private nint _card;
    private int _activeProtocol;

    /// <summary>Creates a reader, optionally pinned to a named reader (else the first found).</summary>
    /// <param name="preferredReader">The PC/SC reader name, or null to use the first available.</param>
    public PcscCardReader(string? preferredReader = null) => _preferredReader = preferredReader;

    /// <inheritdoc />
    public event EventHandler<CardPresenceChangedEventArgs>? CardPresenceChanged;

    /// <inheritdoc />
    public Task<Result<bool, CardReaderError>> WaitForCardAsync(CancellationToken cancellationToken) =>
        Task.Run(() => WaitForCard(cancellationToken), cancellationToken);

    private Result<bool, CardReaderError> WaitForCard(CancellationToken cancellationToken)
    {
        if (_context == 0)
        {
            int ctx = SCardEstablishContext(SCARD_SCOPE_SYSTEM, 0, 0, out _context);
            if (ctx != SCARD_S_SUCCESS)
            {
                return Fail(CardReaderError.NotConnected);
            }
        }

        var readerResult = ResolveReader();
        if (readerResult.IsError)
        {
            return Result<bool, CardReaderError>.Fail(readerResult.Error);
        }

        string reader = readerResult.Value;

        var state = new[]
        {
            new SCARD_READERSTATE
            {
                szReader = reader,
                dwCurrentState = SCARD_STATE_UNAWARE,
                rgbAtr = new byte[36],
            },
        };

        // Poll with a 250 ms timeout per iteration so the token is checked promptly.
        while (!cancellationToken.IsCancellationRequested)
        {
            int rc = SCardGetStatusChange(_context, 250, state, state.Length);
            if (rc == SCARD_E_TIMEOUT)
            {
                continue;
            }

            if (rc != SCARD_S_SUCCESS)
            {
                return Fail(CardReaderError.ReaderFault);
            }

            state[0].dwCurrentState = state[0].dwEventState;
            if ((state[0].dwEventState & SCARD_STATE_PRESENT) != 0)
            {
                return Connect(reader);
            }
        }

        return Fail(CardReaderError.Timeout);
    }

    private Result<bool, CardReaderError> Connect(string reader)
    {
        int rc = SCardConnect(_context, reader, SCARD_SHARE_SHARED,
            SCARD_PROTOCOL_T0 | SCARD_PROTOCOL_T1, out _card, out _activeProtocol);
        if (rc != SCARD_S_SUCCESS)
        {
            return Fail(CardReaderError.ReaderFault);
        }

        CardPresenceChanged?.Invoke(this, new CardPresenceChangedEventArgs(isPresent: true));
        return Result<bool, CardReaderError>.Ok(true);
    }

    /// <inheritdoc />
    public Task<Result<ApduResponse, CardReaderError>> TransmitAsync(
        ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken) =>
        Task.Run(() => Transmit(apdu), cancellationToken);

    private Result<ApduResponse, CardReaderError> Transmit(ReadOnlyMemory<byte> apdu)
    {
        if (_card == 0)
        {
            return Result<ApduResponse, CardReaderError>.Fail(CardReaderError.NotConnected);
        }

        var sendPci = new SCARD_IO_REQUEST
        {
            dwProtocol = _activeProtocol,
            cbPciLength = Marshal.SizeOf<SCARD_IO_REQUEST>(),
        };

        byte[] send = apdu.ToArray();
        byte[] recv = new byte[258]; // 256 data + 2 status word bytes, the short-APDU maximum
        int recvLen = recv.Length;

        int rc = SCardTransmit(_card, in sendPci, send, send.Length, 0, recv, ref recvLen);
        if (rc != SCARD_S_SUCCESS)
        {
            return Result<ApduResponse, CardReaderError>.Fail(CardReaderError.CardRemoved);
        }

        return Result<ApduResponse, CardReaderError>.Ok(new ApduResponse(recv.AsMemory(0, recvLen)));
    }

    private Result<string, CardReaderError> ResolveReader()
    {
        if (_preferredReader is not null)
        {
            return Result<string, CardReaderError>.Ok(_preferredReader);
        }

        int len = 0;
        int rc = SCardListReaders(_context, null, null, ref len);
        if (rc == SCARD_E_NO_READERS_AVAILABLE || len <= 1)
        {
            return Result<string, CardReaderError>.Fail(CardReaderError.NotConnected);
        }

        if (rc != SCARD_S_SUCCESS)
        {
            return Result<string, CardReaderError>.Fail(CardReaderError.ReaderFault);
        }

        byte[] buffer = new byte[len];
        rc = SCardListReaders(_context, null, buffer, ref len);
        if (rc != SCARD_S_SUCCESS)
        {
            return Result<string, CardReaderError>.Fail(CardReaderError.ReaderFault);
        }

        // The buffer is a NUL-separated, double-NUL-terminated multi-string; take the first.
        string first = Encoding.ASCII.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)[0];
        return Result<string, CardReaderError>.Ok(first);
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        if (_card != 0)
        {
            _ = SCardDisconnect(_card, SCARD_LEAVE_CARD);
            _card = 0;
            CardPresenceChanged?.Invoke(this, new CardPresenceChangedEventArgs(isPresent: false));
        }
    }

    /// <summary>Disconnects any card and releases the PC/SC context.</summary>
    public void Dispose()
    {
        Disconnect();
        if (_context != 0)
        {
            _ = SCardReleaseContext(_context);
            _context = 0;
        }
    }

    private static Result<bool, CardReaderError> Fail(CardReaderError error) =>
        Result<bool, CardReaderError>.Fail(error);
}
