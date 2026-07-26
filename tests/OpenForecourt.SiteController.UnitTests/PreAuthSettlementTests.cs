using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Orchestration;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

/// <summary>
/// With firmware pumps on the path, an approval is a pre-authorisation: the customer is
/// authorised up to an amount and the site settles what the meter actually delivered.
/// </summary>
public sealed class PreAuthSettlementTests
{
    private static SiteHarness PreAuthHarness() => new(new InMemoryJournal(), new SiteOptions
    {
        Currency = "GBP",
        PumpCount = 8,
        FloorLimitMinor = 5000,
        OfflineExposureCeilingMinor = 1_000_000,
        SettleOnDispenseComplete = true,
    });

    [Fact]
    public async Task An_approval_stays_open_until_the_dispenser_reports_the_delivery()
    {
        var harness = PreAuthHarness();

        var result = await harness.Service.AuthoriseAsync(harness.Request(1, 5000), default);

        Assert.Equal(AuthorisationDecision.Approved, result.Decision);
        var tx = await harness.Journal.TryGetAsync(result.TransactionId, default);
        Assert.Equal(TransactionStatus.Approved, tx!.Status);
        Assert.Empty(harness.Dispatch.Published); // nothing settled yet
    }

    [Fact]
    public async Task Settlement_is_for_the_delivered_value_not_the_authorised_amount()
    {
        var harness = PreAuthHarness();
        var result = await harness.Service.AuthoriseAsync(harness.Request(1, 5000), default);

        await harness.Service.CompleteFuellingAsync(result.TransactionId, new Money(3175, "GBP"), default);

        var tx = await harness.Journal.TryGetAsync(result.TransactionId, default);
        Assert.Equal(TransactionStatus.Completed, tx!.Status);
        Assert.Equal(3175, tx.Amount.Minor);
        Assert.Single(harness.Dispatch.Published);
    }

    // The preset is what stops the pump, so delivering more than was authorised means the meter
    // is wrong. Settling the larger figure would over-charge the customer.
    [Fact]
    public async Task A_delivery_above_the_authorisation_settles_the_authorised_amount()
    {
        var harness = PreAuthHarness();
        var result = await harness.Service.AuthoriseAsync(harness.Request(1, 5000), default);

        await harness.Service.CompleteFuellingAsync(result.TransactionId, new Money(9999, "GBP"), default);

        var tx = await harness.Journal.TryGetAsync(result.TransactionId, default);
        Assert.Equal(5000, tx!.Amount.Minor);
    }

    // The firmware re-emits DISPENSE_COMPLETE until it is acked (docs/protocol-pump.md §4.4),
    // so a second completion for the same transaction must not settle twice.
    [Fact]
    public async Task A_repeated_dispense_complete_does_not_settle_twice()
    {
        var harness = PreAuthHarness();
        var result = await harness.Service.AuthoriseAsync(harness.Request(1, 5000), default);

        await harness.Service.CompleteFuellingAsync(result.TransactionId, new Money(3175, "GBP"), default);
        await harness.Service.CompleteFuellingAsync(result.TransactionId, new Money(3175, "GBP"), default);

        Assert.Single(harness.Dispatch.Published);
    }

    // An offline approval is its own durable queue awaiting replay; a dispense completion must
    // not short-circuit that and settle it locally.
    [Fact]
    public async Task An_offline_approval_is_left_for_the_replayer()
    {
        var harness = PreAuthHarness();
        harness.Host.Available = false;
        harness.Availability.MarkUnavailable();
        var result = await harness.Service.AuthoriseAsync(harness.Request(1, 2500), default);
        Assert.Equal(AuthorisationDecision.ApprovedOffline, result.Decision);

        await harness.Service.CompleteFuellingAsync(result.TransactionId, new Money(1000, "GBP"), default);

        var tx = await harness.Journal.TryGetAsync(result.TransactionId, default);
        Assert.Equal(TransactionStatus.Approved, tx!.Status);
        Assert.True(tx.Offline);
    }

    [Fact]
    public async Task Completing_an_unknown_transaction_is_a_no_op()
    {
        var harness = PreAuthHarness();

        await harness.Service.CompleteFuellingAsync(Guid.NewGuid(), new Money(1000, "GBP"), default);

        Assert.Empty(harness.Dispatch.Published);
    }
}
