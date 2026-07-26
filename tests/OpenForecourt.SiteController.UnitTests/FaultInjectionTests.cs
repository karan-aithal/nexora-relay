using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.SiteController.Api;
using OpenForecourt.SiteController.Faults;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

public sealed class FaultInjectionTests
{
    private static readonly FinancialRequest Request = new(Guid.NewGuid(), new Money(2500, "GBP"));

    // A severed link must look like silence, not an exception, so the caller takes the same
    // "host state unknown" path a cut cable produces.
    [Fact]
    public async Task A_severed_host_link_yields_no_response_and_fails_the_probe()
    {
        var inner = new InProcHostConnection();
        var faults = new FaultInjector();
        var link = new FaultingHostConnection(inner, inner, faults, new FakeClock(DateTimeOffset.UnixEpoch));

        faults.SetHostLinkDown(true);
        var response = await link.SendAsync(Request, default);

        Assert.Equal(AuthorisationOutcome.NoResponse, response.Outcome);
        Assert.False(await link.PingAsync(default));
        Assert.Empty(inner.Requests); // nothing was even attempted on the real adapter

        faults.SetHostLinkDown(false);
        Assert.Equal(AuthorisationOutcome.Approved, (await link.SendAsync(Request, default)).Outcome);
        Assert.True(await link.PingAsync(default));
    }

    // "Suspend the site controller mid-authorisation" must actually hold the request between
    // the journal write and the wire, and release every held caller on resume.
    [Fact]
    public async Task Suspending_holds_the_host_request_until_resumed()
    {
        var inner = new InProcHostConnection();
        var faults = new FaultInjector();
        var link = new FaultingHostConnection(inner, inner, faults, new FakeClock(DateTimeOffset.UnixEpoch));

        faults.Suspend();
        var held = link.SendAsync(Request, default);

        await Task.Delay(50);
        Assert.False(held.IsCompleted);
        Assert.Empty(inner.Requests);

        faults.Resume();
        var response = await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AuthorisationOutcome.Approved, response.Outcome);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task A_suspension_is_cancellable_so_a_shutdown_is_not_blocked()
    {
        var inner = new InProcHostConnection();
        var faults = new FaultInjector();
        var link = new FaultingHostConnection(inner, inner, faults, new FakeClock(DateTimeOffset.UnixEpoch));
        using var cts = new CancellationTokenSource();

        faults.Suspend();
        var held = link.SendAsync(Request, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
    }

    // CRC corruption is single-shot: it corrupts the next frame, then the retransmission gets
    // through. Otherwise the link would never recover and the fault would be indistinguishable
    // from a dead pump.
    [Fact]
    public void Crc_corruption_is_armed_once_and_consumed_once()
    {
        var faults = new FaultInjector();

        faults.ArmCrcCorruption(3);

        Assert.Contains(3, faults.Snapshot().CrcArmedPumps);
        Assert.True(faults.ConsumeCrcCorruption(3));
        Assert.False(faults.ConsumeCrcCorruption(3));
        Assert.Empty(faults.Snapshot().CrcArmedPumps);
    }

    [Fact]
    public void Arming_a_fault_publishes_the_new_state_to_the_dashboard()
    {
        var faults = new FaultInjector();
        FaultState? published = null;
        faults.Changed += (_, state) => published = state;

        faults.SetHostLatency(750);

        Assert.NotNull(published);
        Assert.Equal(750, published.HostLatencyMs);
    }

    // Forcing a response code works by choosing the PAN the acquirer's rules decline, so the
    // decline is genuinely produced by the host. These PANs must match hostrules.json.
    [Theory]
    [InlineData("51", HostResponsePans.InsufficientFunds)]
    [InlineData("05", HostResponsePans.DoNotHonour)]
    [InlineData("54", HostResponsePans.ExpiredCard)]
    [InlineData("91", HostResponsePans.NoResponse)]
    [InlineData("00", "4111111111111111")]
    public void A_forced_response_code_maps_to_the_test_pan_that_provokes_it(string code, string expected)
    {
        Assert.Equal(expected, HostResponsePans.PanFor(code, "4111111111111111"));
    }
}
