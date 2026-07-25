using Microsoft.Extensions.Logging.Abstractions;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.SiteController.Api;
using OpenForecourt.SiteController.Services;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

public sealed class IdempotencyTests
{
    // CLAUDE.md Phase 5: replay the same request 5 times, assert one transaction.
    [Fact]
    public async Task Same_idempotency_key_authorises_exactly_once()
    {
        var harness = new SiteHarness(new InMemoryJournal());
        var store = new IdempotencyStore();
        var request = harness.Request(pumpId: 1, amountMinor: 2500);

        var results = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var result = await store.GetOrAddAsync("key-1", () => harness.Service.AuthoriseAsync(request, default));
            results.Add(result.TransactionId);
        }

        Assert.Single(results.Distinct());               // all five returned the same transaction
        Assert.Single(harness.Host.Requests);            // the host was asked exactly once
        var all = await harness.Journal.ReadAllAsync(default);
        Assert.Single(all);                              // one journal transaction, settled
        Assert.Equal(TransactionStatus.Completed, all[0].Status);
    }

    [Fact]
    public async Task Concurrent_same_key_requests_share_one_execution()
    {
        var harness = new SiteHarness(new InMemoryJournal());
        var store = new IdempotencyStore();
        var request = harness.Request(pumpId: 2, amountMinor: 1000);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => store.GetOrAddAsync("key-2", () => harness.Service.AuthoriseAsync(request, default)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Select(r => r.TransactionId).Distinct());
        Assert.Single(harness.Host.Requests);
    }

    // CLAUDE.md Phase 5: consumer idempotency by transaction id.
    [Fact]
    public async Task Settlement_consumer_dedupes_repeated_delivery()
    {
        await using var dispatch = new InProcDispatch();
        var consumer = new SettlementConsumer(dispatch, NullLogger<SettlementConsumer>.Instance);
        await consumer.StartAsync(default);

        var txId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            await dispatch.PublishAsync(new DispatchMessage(txId, "settlement", "{}"), default);
        }

        await WaitUntilAsync(() => consumer.SettledCount == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, consumer.SettledCount);
        await consumer.StopAsync(default);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }
    }
}
