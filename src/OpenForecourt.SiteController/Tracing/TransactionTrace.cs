
namespace OpenForecourt.SiteController.Tracing;

/// <summary>One decoded name/value pair inside a trace step (an EMV tag, an ISO 8583 field).</summary>
/// <param name="Name">Field or tag label, already numbered where the protocol numbers things.</param>
/// <param name="Value">The value, masked if the protocol marks the field sensitive.</param>
public sealed record TraceField(string Name, string Value);

/// <summary>
/// One observable exchange in a transaction's life: a card APDU, an ISO 8583 message, an
/// OFP-1 pump command, or a lifecycle marker.
/// </summary>
/// <param name="Seq">1-based ordinal within the trace.</param>
/// <param name="Kind">One of <c>apdu</c>, <c>iso8583</c>, <c>pump</c>, <c>lifecycle</c>.</param>
/// <param name="Label">Short human label, e.g. <c>SELECT</c> or <c>0200 Financial request</c>.</param>
/// <param name="ElapsedMs">Milliseconds from the first step in the trace — the waterfall offset.</param>
/// <param name="DurationMs">How long the exchange itself took — the waterfall bar width.</param>
/// <param name="Hex">Raw wire bytes, uppercase hex, where the exchange has any.</param>
/// <param name="Fields">The decoded view of <paramref name="Hex"/>.</param>
public sealed record TraceStep(
    int Seq, string Kind, string Label, double ElapsedMs, double DurationMs, string? Hex, IReadOnlyList<TraceField> Fields);

/// <summary>A whole transaction trace, ready for the dashboard's detail drawer.</summary>
/// <param name="TransactionId">The transaction, or <see cref="Guid.Empty"/> for an unbound trace.</param>
/// <param name="StartedAt">When the first step was recorded.</param>
/// <param name="TotalMs">Wall-clock span from the first step to the end of the last.</param>
/// <param name="Steps">Every step, in time order.</param>
public sealed record TransactionTraceView(
    Guid TransactionId, DateTimeOffset StartedAt, double TotalMs, IReadOnlyList<TraceStep> Steps);

/// <summary>
/// The mutable accumulator behind one transaction's trace.
/// </summary>
/// <remarks>
/// Steps carry an <b>absolute</b> timestamp rather than a pre-computed offset. That is what makes
/// <see cref="Merge"/> correct: the OPT records APDU exchanges before a transaction id exists, and
/// those steps are merged into the host-side trace afterwards. Sorting by absolute time reassembles
/// the true order regardless of which half was recorded first.
/// </remarks>
public sealed class TransactionTrace
{
    private readonly List<(DateTimeOffset At, TimeSpan Duration, string Kind, string Label, string? Hex, IReadOnlyList<TraceField> Fields)> _steps = [];
    private readonly Lock _gate = new();

    /// <summary>Records one exchange.</summary>
    public void Add(DateTimeOffset at, TimeSpan duration, string kind, string label, string? hex = null, IReadOnlyList<TraceField>? fields = null)
    {
        lock (_gate)
        {
            _steps.Add((at, duration, kind, label, hex, fields ?? []));
        }
    }

    /// <summary>Folds another trace's steps into this one; time order is restored on projection.</summary>
    public void Merge(TransactionTrace other)
    {
        ArgumentNullException.ThrowIfNull(other);
        List<(DateTimeOffset, TimeSpan, string, string, string?, IReadOnlyList<TraceField>)> copy;
        lock (other._gate)
        {
            copy = [.. other._steps];
        }

        lock (_gate)
        {
            _steps.AddRange(copy);
        }
    }

    /// <summary>Projects the trace, ordered by time, with waterfall offsets computed.</summary>
    public TransactionTraceView ToView(Guid transactionId)
    {
        lock (_gate)
        {
            if (_steps.Count == 0)
            {
                return new TransactionTraceView(transactionId, DateTimeOffset.MinValue, 0, []);
            }

            var ordered = _steps.OrderBy(s => s.At).ToArray();
            var start = ordered[0].At;
            var steps = ordered
                .Select((s, i) => new TraceStep(
                    i + 1, s.Kind, s.Label,
                    (s.At - start).TotalMilliseconds, s.Duration.TotalMilliseconds, s.Hex, s.Fields))
                .ToArray();
            double total = steps.Max(s => s.ElapsedMs + s.DurationMs);
            return new TransactionTraceView(transactionId, start, total, steps);
        }
    }
}

/// <summary>
/// Where components push trace steps. Kept as a port so <see cref="Hosting.TcpHostConnection"/>
/// can record the ISO 8583 exchange without knowing anything about the dashboard.
/// </summary>
public interface ITraceSink
{
    /// <summary>Returns the trace for a transaction, creating it if this is its first step.</summary>
    TransactionTrace TraceFor(Guid transactionId);
}

/// <summary>
/// Holds the most recent transaction traces in memory, oldest evicted first.
/// </summary>
/// <remarks>
/// Traces are a diagnostic view, not a record of account: they are deliberately volatile and
/// bounded, so a long-running forecourt cannot grow them without limit. The durable record is the
/// journal. Nothing here holds a PAN — the decoders mask before the value ever reaches a step.
/// </remarks>
public sealed class TraceStore : ITraceSink
{
    /// <summary>How many transaction traces are retained.</summary>
    public const int Capacity = 200;

    private readonly Dictionary<Guid, TransactionTrace> _traces = [];
    private readonly Queue<Guid> _order = new();
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public TransactionTrace TraceFor(Guid transactionId)
    {
        lock (_gate)
        {
            if (_traces.TryGetValue(transactionId, out var existing))
            {
                return existing;
            }

            var trace = new TransactionTrace();
            _traces[transactionId] = trace;
            _order.Enqueue(transactionId);
            while (_order.Count > Capacity)
            {
                _traces.Remove(_order.Dequeue());
            }

            return trace;
        }
    }

    /// <summary>Merges a trace recorded before the transaction id existed (the OPT's APDU exchange).</summary>
    public void Attach(Guid transactionId, TransactionTrace pending) => TraceFor(transactionId).Merge(pending);

    /// <summary>Returns a transaction's trace, or null if it has been evicted or never existed.</summary>
    public TransactionTraceView? TryGet(Guid transactionId)
    {
        lock (_gate)
        {
            return _traces.TryGetValue(transactionId, out var trace) ? trace.ToView(transactionId) : null;
        }
    }
}
