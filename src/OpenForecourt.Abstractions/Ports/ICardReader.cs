namespace OpenForecourt.Abstractions.Ports;

/// <summary>Ways a card-reader operation can fail. Modelled as data, not exceptions.</summary>
public enum CardReaderError
{
    /// <summary>No card was presented within the wait window.</summary>
    Timeout,

    /// <summary>A card was present but was removed mid-exchange.</summary>
    CardRemoved,

    /// <summary>The reader hardware/driver reported a fault (e.g. PC/SC protocol error).</summary>
    ReaderFault,

    /// <summary>The reader is not connected or has been disconnected.</summary>
    NotConnected,
}

/// <summary>The outcome of an APDU exchange: the response bytes, or a reader error.</summary>
public readonly record struct ApduResponse(ReadOnlyMemory<byte> Data);

/// <summary>Event payload describing a change in card presence.</summary>
public sealed class CardPresenceChangedEventArgs(bool isPresent) : EventArgs
{
    /// <summary>True if a card is now in the field, false if it was removed.</summary>
    public bool IsPresent { get; } = isPresent;
}

/// <summary>
/// A smart-card reader boundary. Implementations include the real PC/SC reader
/// (<c>winscard.dll</c> P/Invoke, Windows), a virtual PC/SC reader over TCP, and an
/// in-process reader that calls straight into a simulated card (for CI).
/// </summary>
/// <remarks>
/// Reader errors are returned as <see cref="Result{TValue,TError}"/>, never thrown: a
/// card being pulled out mid-read is an expected operational event, not an exceptional
/// one (CLAUDE.md section 6). Implementations must be safe to call from a single reader
/// pump loop; concurrent <see cref="TransmitAsync"/> calls are not required to be supported.
/// </remarks>
public interface ICardReader
{
    /// <summary>Raised when a card enters or leaves the reader field.</summary>
    event EventHandler<CardPresenceChangedEventArgs> CardPresenceChanged;

    /// <summary>
    /// Waits until a card is present and a connection to it is established, or the token
    /// is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Success once a card is connected, or a <see cref="CardReaderError"/>.</returns>
    Task<Result<bool, CardReaderError>> WaitForCardAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Transmits a command APDU to the connected card and returns the response APDU.
    /// </summary>
    /// <param name="apdu">The command APDU bytes.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The response APDU, or a <see cref="CardReaderError"/> (e.g. <see cref="CardReaderError.CardRemoved"/>).</returns>
    Task<Result<ApduResponse, CardReaderError>> TransmitAsync(
        ReadOnlyMemory<byte> apdu,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drops the connection to the current card. Idempotent; safe to call when no card is
    /// connected. Does not throw for an already-disconnected reader.
    /// </summary>
    void Disconnect();
}
