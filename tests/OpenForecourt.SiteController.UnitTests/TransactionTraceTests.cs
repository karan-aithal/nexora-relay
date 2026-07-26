using OpenForecourt.SiteController.Tracing;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

public sealed class TransactionTraceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    // The OPT records the card exchange before a transaction id exists and merges it in
    // afterwards, so the merged trace must reassemble true time order — not append order.
    [Fact]
    public void Merged_trace_is_ordered_by_time_not_by_merge_order()
    {
        var store = new TraceStore();
        var id = Guid.NewGuid();

        // The host half is recorded first (it is what creates the trace), at t+500ms.
        store.TraceFor(id).Add(Origin.AddMilliseconds(500), TimeSpan.FromMilliseconds(120), "iso8583", "0200 request");

        // The card half happened earlier but is merged later.
        var opt = new TransactionTrace();
        opt.Add(Origin, TimeSpan.FromMilliseconds(10), "apdu", "SELECT");
        opt.Add(Origin.AddMilliseconds(20), TimeSpan.FromMilliseconds(10), "apdu", "GENERATE AC");
        store.Attach(id, opt);

        var view = store.TryGet(id);

        Assert.NotNull(view);
        Assert.Equal(["SELECT", "GENERATE AC", "0200 request"], view.Steps.Select(s => s.Label));
        Assert.Equal([1, 2, 3], view.Steps.Select(s => s.Seq));
    }

    // The waterfall needs offsets relative to the first step and a total that includes the
    // duration of the last one — otherwise a slow final host response renders as zero width.
    [Fact]
    public void Waterfall_offsets_are_relative_to_the_first_step_and_total_covers_the_last()
    {
        var trace = new TransactionTrace();
        trace.Add(Origin, TimeSpan.FromMilliseconds(10), "apdu", "SELECT");
        trace.Add(Origin.AddMilliseconds(400), TimeSpan.FromMilliseconds(250), "iso8583", "0200 request");

        var view = trace.ToView(Guid.Empty);

        Assert.Equal(0, view.Steps[0].ElapsedMs);
        Assert.Equal(400, view.Steps[1].ElapsedMs);
        Assert.Equal(650, view.TotalMs); // 400ms offset + 250ms duration
        Assert.Equal(Origin, view.StartedAt);
    }

    // Traces are a bounded diagnostic, not a record of account: a long-running forecourt must
    // not grow them without limit.
    [Fact]
    public void Store_evicts_the_oldest_trace_beyond_capacity()
    {
        var store = new TraceStore();
        var first = Guid.NewGuid();
        store.TraceFor(first).Add(Origin, TimeSpan.Zero, "lifecycle", "opened");

        for (int i = 0; i < TraceStore.Capacity; i++)
        {
            store.TraceFor(Guid.NewGuid()).Add(Origin, TimeSpan.Zero, "lifecycle", "opened");
        }

        Assert.Null(store.TryGet(first));
    }

    [Fact]
    public void Unknown_transaction_has_no_trace()
    {
        var store = new TraceStore();

        Assert.Null(store.TryGet(Guid.NewGuid()));
    }
}
