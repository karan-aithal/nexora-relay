namespace OpenForecourt.Abstractions.Ports;

/// <summary>
/// One message on the store-and-forward path: a completed transaction (or an advice/
/// reversal) that must reach the settlement consumer at least once.
/// </summary>
/// <param name="TransactionId">Correlates the message to its transaction; the consumer dedupes on this.</param>
/// <param name="Kind">A short discriminator, e.g. <c>settlement</c> or <c>reversal-advice</c>.</param>
/// <param name="Payload">The message body (already free of any PAN).</param>
public readonly record struct DispatchMessage(Guid TransactionId, string Kind, string Payload);

/// <summary>What a consumer decided about a delivered <see cref="DispatchMessage"/>.</summary>
public enum DispatchResult
{
    /// <summary>Processed (or a duplicate already processed): remove from the queue.</summary>
    Ack,

    /// <summary>Transient failure: return to the queue to try again.</summary>
    Retry,

    /// <summary>Un-processable: route to the dead-letter queue and stop retrying.</summary>
    DeadLetter,
}

/// <summary>
/// The store-and-forward dispatch path for completed transactions.
/// </summary>
/// <remarks>
/// <para>
/// The production implementation is RabbitMQ with a durable queue and a durable dead-letter
/// queue; an in-process implementation backs CI, where no broker runs (CLAUDE.md section 2,
/// "CI must be able to run the full test suite on Linux"). Both are the same port, so the
/// orchestration code cannot tell which is behind it.
/// </para>
/// <para>
/// Delivery is at-least-once, so the consumer must be idempotent: it dedupes on
/// <see cref="DispatchMessage.TransactionId"/> and returns <see cref="DispatchResult.Ack"/>
/// for a message it has already handled.
/// </para>
/// </remarks>
public interface ITransactionDispatch : IAsyncDisposable
{
    /// <summary>Durably enqueues a message for the settlement consumer.</summary>
    Task PublishAsync(DispatchMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Starts delivering queued messages to <paramref name="handler"/>. The handler's return
    /// value decides acknowledgement, redelivery or dead-lettering. Dispose the returned
    /// subscription to stop consuming.
    /// </summary>
    IAsyncDisposable Subscribe(Func<DispatchMessage, CancellationToken, Task<DispatchResult>> handler);
}
