using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Adapters.InProc;

/// <summary>
/// An in-process <see cref="ITransactionDispatch"/>: a bounded channel modelling the
/// RabbitMQ store-and-forward queue, plus a dead-letter list, for CI (CLAUDE.md section 2).
/// </summary>
/// <remarks>
/// It reproduces the queue <i>semantics</i> the orchestration relies on — at-least-once
/// delivery, redelivery on <see cref="DispatchResult.Retry"/>, dead-lettering on
/// <see cref="DispatchResult.DeadLetter"/> or after too many retries — without a broker. It
/// is not durable across process exit; that is the SQLite journal's job, not the queue's.
/// </remarks>
public sealed class InProcDispatch : ITransactionDispatch
{
    private const int MaxDeliveries = 5;

    private readonly Channel<Delivery> _queue = Channel.CreateUnbounded<Delivery>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<DispatchMessage> _deadLetters = new();
    private readonly ConcurrentQueue<DispatchMessage> _published = new();

    /// <summary>Messages that were dead-lettered, for test assertions.</summary>
    public IReadOnlyCollection<DispatchMessage> DeadLetters => _deadLetters.ToArray();

    /// <summary>Every message published, in order, for test assertions. Independent of consumption.</summary>
    public IReadOnlyList<DispatchMessage> Published => [.. _published];

    /// <inheritdoc />
    public Task PublishAsync(DispatchMessage message, CancellationToken cancellationToken)
    {
        _published.Enqueue(message);
        return _queue.Writer.WriteAsync(new Delivery(message, 1), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public IAsyncDisposable Subscribe(Func<DispatchMessage, CancellationToken, Task<DispatchResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var cts = new CancellationTokenSource();
        var loop = ConsumeAsync(handler, cts.Token);
        return new Subscription(cts, loop);
    }

    private async Task ConsumeAsync(
        Func<DispatchMessage, CancellationToken, Task<DispatchResult>> handler, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var delivery in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                DispatchResult result;
                try
                {
                    result = await handler(delivery.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // a handler fault is a transient failure: redeliver, don't kill the consumer
                catch
#pragma warning restore CA1031
                {
                    result = DispatchResult.Retry;
                }

                switch (result)
                {
                    case DispatchResult.Ack:
                        break;
                    case DispatchResult.Retry when delivery.Deliveries < MaxDeliveries:
                        await _queue.Writer.WriteAsync(
                            delivery with { Deliveries = delivery.Deliveries + 1 }, cancellationToken).ConfigureAwait(false);
                        break;
                    default: // DeadLetter, or Retry past the redelivery limit
                        _deadLetters.Enqueue(delivery.Message);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Subscription disposed.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private readonly record struct Delivery(DispatchMessage Message, int Deliveries);

    private sealed class Subscription(CancellationTokenSource cts, Task loop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }

            cts.Dispose();
        }
    }
}
