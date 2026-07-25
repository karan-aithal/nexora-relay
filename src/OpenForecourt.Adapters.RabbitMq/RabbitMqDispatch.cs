using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Adapters.RabbitMq;

/// <summary>
/// The production <see cref="ITransactionDispatch"/>: a durable RabbitMQ queue for
/// store-and-forward settlement, backed by a durable dead-letter queue.
/// </summary>
/// <remarks>
/// <para>
/// The main queue is declared with a dead-letter exchange, so a message the consumer
/// rejects without requeue (<see cref="DispatchResult.DeadLetter"/>, or one that exceeds the
/// redelivery limit) lands in <c>settlement.dlq</c> for inspection rather than being lost or
/// spinning forever. Messages are published persistent and the queues are durable, so a
/// broker restart does not drop in-flight settlements.
/// </para>
/// <para>
/// Redelivery count is read from the AMQP <c>x-death</c> header the broker stamps when a
/// message is dead-lettered and requeued; past <see cref="MaxDeliveries"/> the message is
/// dead-lettered for good.
/// </para>
/// </remarks>
public sealed class RabbitMqDispatch : ITransactionDispatch
{
    /// <summary>The durable store-and-forward queue.</summary>
    public const string Queue = "settlement";

    /// <summary>The durable dead-letter queue.</summary>
    public const string DeadLetterQueue = "settlement.dlq";

    private const int MaxDeliveries = 5;

    private readonly IConnection _connection;
    private readonly IChannel _publishChannel;
    private readonly List<Subscription> _subscriptions = [];

    private RabbitMqDispatch(IConnection connection, IChannel publishChannel)
    {
        _connection = connection;
        _publishChannel = publishChannel;
    }

    /// <summary>Connects to the broker and declares the queues. Idempotent on the broker side.</summary>
    /// <param name="uri">The AMQP URI, e.g. <c>amqp://guest:guest@rabbitmq:5672</c>.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public static async Task<RabbitMqDispatch> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var factory = new ConnectionFactory { Uri = uri };
        var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = DeadLetterQueue,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new RabbitMqDispatch(connection, channel);
    }

    /// <inheritdoc />
    public async Task PublishAsync(DispatchMessage message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = message.TransactionId.ToString(),
            Type = message.Kind,
        };
        await _publishChannel.BasicPublishAsync(
            exchange: string.Empty, routingKey: Queue, mandatory: false,
            basicProperties: properties, body: body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncDisposable Subscribe(Func<DispatchMessage, CancellationToken, Task<DispatchResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(_connection, handler);
        lock (_subscriptions)
        {
            _subscriptions.Add(subscription);
        }

        _ = subscription.StartAsync();
        return subscription;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Subscription[] subs;
        lock (_subscriptions)
        {
            subs = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var sub in subs)
        {
            await sub.DisposeAsync().ConfigureAwait(false);
        }

        await _publishChannel.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Subscription(
        IConnection connection,
        Func<DispatchMessage, CancellationToken, Task<DispatchResult>> handler) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private IChannel? _channel;

        public async Task StartAsync()
        {
            _channel = await connection.CreateChannelAsync(cancellationToken: _cts.Token).ConfigureAwait(false);
            // One unacked message at a time keeps a slow settlement consumer from swallowing the queue.
            await _channel.BasicQosAsync(0, prefetchCount: 1, global: false, _cts.Token).ConfigureAwait(false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += OnReceivedAsync;
            await _channel.BasicConsumeAsync(Queue, autoAck: false, consumer, _cts.Token).ConfigureAwait(false);
        }

        private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs args)
        {
            if (_channel is null)
            {
                return;
            }

            DispatchResult result;
            try
            {
                var message = JsonSerializer.Deserialize<DispatchMessage>(
                    Encoding.UTF8.GetString(args.Body.Span));
                result = await handler(message, _cts.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // a handler fault is transient: requeue, don't tear down the consumer
            catch when (!_cts.IsCancellationRequested)
#pragma warning restore CA1031
            {
                result = DispatchResult.Retry;
            }

            switch (result)
            {
                case DispatchResult.Ack:
                    await _channel.BasicAckAsync(args.DeliveryTag, multiple: false, _cts.Token).ConfigureAwait(false);
                    break;
                case DispatchResult.Retry when Deaths(args) < MaxDeliveries:
                    // Requeue for another attempt (nack with requeue keeps it on the main queue).
                    await _channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true, _cts.Token).ConfigureAwait(false);
                    break;
                default: // DeadLetter, or Retry past the limit: reject without requeue -> DLX
                    await _channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, _cts.Token).ConfigureAwait(false);
                    break;
            }
        }

        private static int Deaths(BasicDeliverEventArgs args)
        {
            if (args.BasicProperties.Headers?.TryGetValue("x-death", out var raw) == true &&
                raw is IList<object> deaths && deaths.Count > 0 &&
                deaths[0] is IDictionary<string, object?> first &&
                first.TryGetValue("count", out var count) && count is long c)
            {
                return (int)c;
            }

            return 0;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_channel is not null)
            {
                await _channel.DisposeAsync().ConfigureAwait(false);
            }

            _cts.Dispose();
        }
    }
}
