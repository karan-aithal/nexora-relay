using OpenForecourt.Abstractions;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.VirtualCard;

namespace OpenForecourt.Adapters.InProc;

/// <summary>
/// An <see cref="ICardReader"/> that talks straight to a <see cref="VirtualCard.VirtualCard"/>
/// in the same process — no sockets, no drivers. This is the reader CI runs against on Linux,
/// where no PC/SC stack is available.
/// </summary>
/// <remarks>
/// It is deliberately the same port the real <c>PcscCardReader</c> implements, so the EMV
/// terminal cannot tell whether it is speaking to a simulated card or an ACR122U on a desk
/// (CLAUDE.md section 3). The card is always "present"; <see cref="Disconnect"/> models a
/// removal so presence-change handling can be exercised.
/// </remarks>
public sealed class InProcCardReader(VirtualCard.VirtualCard card) : ICardReader
{
    private readonly VirtualCard.VirtualCard _card = card;
    private bool _connected;

    /// <inheritdoc />
    public event EventHandler<CardPresenceChangedEventArgs>? CardPresenceChanged;

    /// <inheritdoc />
    public Task<Result<bool, CardReaderError>> WaitForCardAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<bool, CardReaderError>.Fail(CardReaderError.Timeout));
        }

        if (!_connected)
        {
            _connected = true;
            CardPresenceChanged?.Invoke(this, new CardPresenceChangedEventArgs(isPresent: true));
        }

        return Task.FromResult(Result<bool, CardReaderError>.Ok(true));
    }

    /// <inheritdoc />
    public Task<Result<ApduResponse, CardReaderError>> TransmitAsync(
        ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            return Task.FromResult(Result<ApduResponse, CardReaderError>.Fail(CardReaderError.CardRemoved));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<ApduResponse, CardReaderError>.Fail(CardReaderError.Timeout));
        }

        byte[] response = _card.Process(apdu.Span);
        return Task.FromResult(Result<ApduResponse, CardReaderError>.Ok(new ApduResponse(response)));
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        if (_connected)
        {
            _connected = false;
            CardPresenceChanged?.Invoke(this, new CardPresenceChangedEventArgs(isPresent: false));
        }
    }
}
