using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenForecourt.SiteController.Realtime;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

public sealed class BackpressureTests
{
    // CLAUDE.md Phase 5: a slow dashboard client must never block the pump pipeline.
    [Fact]
    public async Task Slow_consumer_does_not_stall_broadcasting_or_other_clients()
    {
        using var release = new ManualResetEventSlim(false);
        var fast = new ConcurrentQueue<int>();

        Task Send(string connectionId, Frame frame, CancellationToken ct)
        {
            if (connectionId == "slow")
            {
                release.Wait(ct); // stuck client: never drains until the test lets it
            }
            else
            {
                fast.Enqueue((int)frame.Payload);
            }

            return Task.CompletedTask;
        }

        var dispatcher = new PerConnectionDispatcher(Send, NullLogger<PerConnectionDispatcher>.Instance);
        dispatcher.Add("slow");
        dispatcher.Add("fast");

        // Broadcasting a flood must return promptly even though "slow" is wedged.
        var stopwatch = Stopwatch.StartNew();
        const int frames = 5000;
        for (int i = 1; i <= frames; i++)
        {
            dispatcher.Broadcast(new Frame("PumpUpdated", i));
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"broadcast stalled: {stopwatch.ElapsedMilliseconds}ms");

        // The fast client keeps up and eventually sees the final frame.
        await WaitUntilAsync(() => fast.Contains(frames), TimeSpan.FromSeconds(5));
        Assert.Contains(frames, fast);

        release.Set();
        await dispatcher.RemoveAsync("slow");
        await dispatcher.RemoveAsync("fast");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "condition not met within timeout");
    }
}
