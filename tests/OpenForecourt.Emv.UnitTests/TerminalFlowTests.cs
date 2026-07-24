using OpenForecourt.Adapters.InProc;
using OpenForecourt.Emv.BerTlv;
using OpenForecourt.Emv.Terminal;
using OpenForecourt.VirtualCard;
using Xunit;

namespace OpenForecourt.Emv.UnitTests;

public sealed class TerminalFlowTests
{
    // A fixed unpredictable number and a fixed clock make every run reproducible.
    private static readonly byte[] FixedUn = [0xDE, 0xAD, 0xBE, 0xEF];
    private static readonly DateTimeOffset During2026 = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset During2031 = new(2031, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static EmvTerminal Terminal(string card, DateTimeOffset now, TerminalConfig? config = null)
    {
        var reader = new InProcCardReader(new VirtualCard.VirtualCard(
            CardProfile.Load(Path.Combine(AppContext.BaseDirectory, "cards", card))));
        return new EmvTerminal(reader, config ?? new TerminalConfig(), new FakeClock(now), FixedUn);
    }

    private static async Task<TerminalOutcome> RunAsync(string card, long amount, DateTimeOffset now, TerminalConfig? config = null)
    {
        var result = await Terminal(card, now, config).RunAsync(amount, CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsError ? result.Error.ToString() : "");
        return result.Value;
    }

    [Fact]
    public async Task Contact_card_below_floor_limit_goes_online_with_online_pin()
    {
        var outcome = await RunAsync("contact-visa-credit.json", amount: 1000, During2026);

        Assert.Equal(CvmMethod.OnlinePin, outcome.Cvm);
        Assert.Equal(CryptogramType.Arqc, outcome.Requested);
        Assert.Equal(TerminalDecision.OnlineAuthorisationRequested, outcome.Decision);

        // Below the floor limit: TVR byte 4 "exceeds floor limit" (0x80) must be clear.
        Assert.Equal(0x00, outcome.Tvr[3] & 0x80);
        // Online PIN entered: TVR byte 3 bit 3 (0x04) set.
        Assert.Equal(0x04, outcome.Tvr[2] & 0x04);
    }

    [Fact]
    public async Task Contact_card_above_floor_limit_sets_floor_limit_bit()
    {
        var outcome = await RunAsync("contact-visa-credit.json", amount: 9000, During2026);

        Assert.Equal(0x80, outcome.Tvr[3] & 0x80); // exceeds floor limit
        Assert.Equal(CryptogramType.Arqc, outcome.Requested);
    }

    [Fact]
    public async Task Expired_card_sets_expired_application_bit()
    {
        // The card expires 2030-12-31; a 2031 transaction date makes it expired.
        var outcome = await RunAsync("contact-visa-credit.json", amount: 1000, During2031);

        Assert.Equal(0x40, outcome.Tvr[1] & 0x40); // processing restrictions: expired application
    }

    [Fact]
    public async Task Contactless_card_selects_no_cvm()
    {
        var outcome = await RunAsync("contactless-mc.json", amount: 500, During2026);

        Assert.Equal(CvmMethod.NoCvm, outcome.Cvm);
        Assert.Equal(TerminalDecision.OnlineAuthorisationRequested, outcome.Decision);
    }

    [Fact]
    public async Task Decline_card_returns_aac_and_signature_cvm()
    {
        var outcome = await RunAsync("decline-card.json", amount: 500, During2026);

        Assert.Equal(CvmMethod.Signature, outcome.Cvm);
        // Terminal still asks for an ARQC; the card overrides with an AAC (offline decline).
        Assert.Equal(CryptogramType.Arqc, outcome.Requested);
        Assert.Equal(TerminalDecision.DeclinedOffline, outcome.Decision);
    }

    [Fact]
    public async Task Pan_is_masked_in_the_outcome()
    {
        var outcome = await RunAsync("contact-visa-credit.json", amount: 1000, During2026);

        Assert.Equal("411111******1111", outcome.Pan.ToString());
        Assert.DoesNotContain("4111111111111111", outcome.Pan.ToString());
    }

    [Fact]
    public async Task Field55_carries_the_cryptogram_and_its_context()
    {
        var outcome = await RunAsync("contact-visa-credit.json", amount: 1000, During2026);

        var tags = BerTlvCodec.TryParse(outcome.Field55).Value.Select(n => n.Tag.ToString()).ToList();
        Assert.Contains("9F26", tags); // Application Cryptogram
        Assert.Contains("9F27", tags); // CID
        Assert.Contains("9F36", tags); // ATC
        Assert.Contains("95", tags);   // TVR
        Assert.Contains("9F37", tags); // Unpredictable Number
        Assert.Contains("82", tags);   // AIP

        // The cryptogram in field 55 is the one the card returned.
        var arqc = BerTlvCodec.TryParse(outcome.Field55).Value.First(n => n.Tag.Value == 0x9F26);
        Assert.Equal("A1B2C3D4E5F60718", Convert.ToHexString(arqc.Value.Span));
    }

    [Fact]
    public async Task Random_selection_selects_below_floor_limit_when_target_is_100()
    {
        var config = new TerminalConfig { RandomSelectionTargetPercent = 100 };
        var outcome = await RunAsync("contact-visa-credit.json", amount: 100, During2026, config);

        Assert.Equal(0x10, outcome.Tvr[3] & 0x10); // selected randomly for online
    }
}
