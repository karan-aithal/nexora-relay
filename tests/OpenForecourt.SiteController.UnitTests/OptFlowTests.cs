using Microsoft.Extensions.Logging.Abstractions;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.Crypto.Tokenization;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Faults;
using OpenForecourt.SiteController.Opt;
using OpenForecourt.SiteController.Simulation;
using OpenForecourt.SiteController.Tracing;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

/// <summary>
/// End-to-end through the outdoor payment terminal: a real card profile, the real EMV kernel,
/// real tokenization, and the trace the dashboard's detail drawer reads.
/// </summary>
public sealed class OptFlowTests
{
    private sealed class Fixture
    {
        public Fixture(SiteOptions? options = null)
        {
            Harness = new SiteHarness(new InMemoryJournal(), options ?? new SiteOptions
            {
                Currency = "GBP",
                PumpCount = 8,
                FloorLimitMinor = 5000,
                OfflineExposureCeilingMinor = 1_000_000,
                CardProfilesPath = Path.Combine(AppContext.BaseDirectory, "cards"),
                PinBdkPath = Path.Combine(AppContext.BaseDirectory, "keys", "bdk.hex"),
            });

            Traces = new TraceStore();
            var grades = new GradeCatalogue(Harness.Options);
            var fleet = new PumpFleet(
                Harness.Options, Harness.Pumps, Harness.Service, new FaultInjector(), Traces, grades,
                Harness.Clock, NullLogger<PumpFleet>.Instance);

            Opt = new OptService(
                new CardCatalogue(Harness.Options, NullLogger<CardCatalogue>.Instance),
                Harness.Service, Harness.Pumps, fleet, Traces, new FpeTokenVault(), grades,
                Harness.Options, Harness.Clock, NullLogger<OptService>.Instance);
        }

        public SiteHarness Harness { get; }

        public TraceStore Traces { get; }

        public OptService Opt { get; }
    }

    // The contactless profile carries no CVM list, so the kernel selects NoCvm and the flow
    // runs straight through to a decision.
    [Fact]
    public async Task Presenting_a_no_cvm_card_runs_the_emv_flow_and_authorises()
    {
        var fixture = new Fixture();

        var result = await fixture.Opt.PresentCardAsync(1, "contactless-mc", 2500, "Contactless", default);

        Assert.True(result.Accepted);
        Assert.NotNull(result.TransactionId);
        Assert.Equal(nameof(OptStage.Approved), fixture.Opt.Get(1).Stage);
    }

    // The Visa profile's CVM list asks for online PIN, so the terminal must stop at the keypad
    // and only go to the acquirer once the PIN has been captured and encrypted.
    [Fact]
    public async Task An_online_pin_card_holds_at_the_keypad_and_resumes_on_the_pin()
    {
        var fixture = new Fixture();

        var presented = await fixture.Opt.PresentCardAsync(1, "contact-visa-credit", 2500, "Contact", default);
        Assert.False(presented.Accepted);

        var screen = fixture.Opt.Get(1);
        Assert.Equal(nameof(OptStage.PinRequired), screen.Stage);
        Assert.Equal("OnlinePin", screen.Cvm);
        Assert.NotNull(screen.DeadlineAt); // the countdown the OPT screen shows

        var authorised = await fixture.Opt.EnterPinAsync(1, "1234", default);

        Assert.True(authorised.Accepted);
        Assert.Equal(nameof(OptStage.Approved), fixture.Opt.Get(1).Stage);
    }

    // CLAUDE.md section 7.5: a PIN block is never logged or traced at any level. The KSN is,
    // because it is what proves the key was derived per transaction.
    [Fact]
    public async Task The_pin_block_never_reaches_the_trace_but_the_ksn_does()
    {
        var fixture = new Fixture();
        await fixture.Opt.PresentCardAsync(1, "contact-visa-credit", 2500, "Contact", default);

        var result = await fixture.Opt.EnterPinAsync(1, "1234", default);
        var trace = fixture.Traces.TryGet(result.TransactionId!.Value)!;

        var pinStep = Assert.Single(trace.Steps, s => s.Label.Contains("PIN", StringComparison.Ordinal));
        Assert.Contains(pinStep.Fields, f => f.Name == "KSN" && f.Value.Length == 20);
        Assert.DoesNotContain(trace.Steps.SelectMany(s => s.Fields), f => f.Value.Contains("1234", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_short_pin_is_refused_without_ending_the_session()
    {
        var fixture = new Fixture();
        await fixture.Opt.PresentCardAsync(1, "contact-visa-credit", 2500, "Contact", default);

        var result = await fixture.Opt.EnterPinAsync(1, "12", default);

        Assert.False(result.Accepted);
        Assert.Equal(nameof(OptStage.PinRequired), fixture.Opt.Get(1).Stage);
    }

    // The flagship screen: the drawer must show the literal card exchange and the literal
    // acquirer message, in the order they happened.
    [Fact]
    public async Task The_transaction_trace_carries_the_apdu_exchange_and_the_terminal_decision()
    {
        var fixture = new Fixture();

        var result = await fixture.Opt.PresentCardAsync(1, "contactless-mc", 2500, "Contactless", default);
        var trace = fixture.Traces.TryGet(result.TransactionId!.Value);

        Assert.NotNull(trace);
        var apdus = trace.Steps.Where(s => s.Kind == "apdu").ToArray();
        Assert.Contains(apdus, s => s.Label.StartsWith("SELECT", StringComparison.Ordinal));
        Assert.Contains(apdus, s => s.Label.StartsWith("GENERATE AC", StringComparison.Ordinal));
        Assert.All(apdus, s => Assert.False(string.IsNullOrEmpty(s.Hex)));
        Assert.Contains(trace.Steps, s => s.Kind == "lifecycle" && s.Label.Contains("Terminal decision", StringComparison.Ordinal));

        // Waterfall offsets are monotonic, which is what makes the timing view readable.
        Assert.Equal(trace.Steps.Select(s => s.ElapsedMs).Order(), trace.Steps.Select(s => s.ElapsedMs));
    }

    // CLAUDE.md section 7.4: cleartext PAN never leaves the OPT boundary. What the orchestrator,
    // the journal and every downstream consumer see is a token.
    [Fact]
    public async Task The_cleartext_pan_never_reaches_the_journal_or_the_decoded_trace()
    {
        var fixture = new Fixture();

        var result = await fixture.Opt.PresentCardAsync(1, "contactless-mc", 2500, "Contactless", default);
        var tx = await fixture.Harness.Journal.TryGetAsync(result.TransactionId!.Value, default);
        var trace = fixture.Traces.TryGet(result.TransactionId.Value)!;

        Assert.NotNull(tx!.Token);
        Assert.DoesNotContain("5555555555554444", tx.Token, StringComparison.Ordinal);

        // The decoded side of every step is masked. The raw hex is the inside of the OPT
        // boundary — a logic-analyser view — and is deliberately not asserted against here.
        var decoded = trace.Steps.SelectMany(s => s.Fields).Select(f => f.Value);
        Assert.DoesNotContain(decoded, v => v.Contains("5555555555554444", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_card_that_declines_offline_never_opens_a_transaction()
    {
        var fixture = new Fixture();

        var result = await fixture.Opt.PresentCardAsync(2, "decline-card", 2500, "Contact", default);

        Assert.False(result.Accepted);
        var all = await fixture.Harness.Journal.ReadAllAsync(default);
        Assert.Empty(all);
    }

    [Fact]
    public async Task A_second_card_is_refused_while_a_session_is_in_progress()
    {
        var fixture = new Fixture();
        await fixture.Opt.PresentCardAsync(1, "contact-visa-credit", 2500, "Contact", default);

        var second = await fixture.Opt.PresentCardAsync(1, "decline-card", 2500, "Contact", default);

        Assert.False(second.Accepted);
        Assert.Equal("Rejected", second.Decision);
    }

    [Fact]
    public async Task An_unknown_card_profile_is_rejected()
    {
        var fixture = new Fixture();

        var result = await fixture.Opt.PresentCardAsync(1, "no-such-card", 2500, "Contact", default);

        Assert.False(result.Accepted);
        Assert.Equal("Rejected", result.Decision);
    }

    [Fact]
    public async Task Cancelling_returns_the_terminal_to_idle()
    {
        var fixture = new Fixture();
        await fixture.Opt.PresentCardAsync(1, "contact-visa-credit", 2500, "Contact", default);

        fixture.Opt.Cancel(1);
        Assert.Equal(nameof(OptStage.Finished), fixture.Opt.Get(1).Stage);

        fixture.Opt.ReturnToIdle(1);
        Assert.Equal(nameof(OptStage.Idle), fixture.Opt.Get(1).Stage);
    }

    [Fact]
    public void A_terminal_with_no_session_reports_the_idle_screen()
    {
        var fixture = new Fixture();

        var terminals = fixture.Opt.Snapshot();

        Assert.Equal(fixture.Harness.Options.PumpCount, terminals.Count);
        Assert.All(terminals, t => Assert.Equal(nameof(OptStage.Idle), t.Stage));
    }

    [Fact]
    public async Task A_pin_cannot_be_submitted_to_a_terminal_that_is_not_asking_for_one()
    {
        var fixture = new Fixture();

        var result = await fixture.Opt.EnterPinAsync(1, "1234", default);

        Assert.False(result.Accepted);
        Assert.Equal("Rejected", result.Decision);
    }
}
