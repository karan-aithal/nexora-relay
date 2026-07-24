using OpenForecourt.Abstractions.Ports;
using OpenForecourt.HostSimulator;
using OpenForecourt.Iso8583;
using Xunit;

namespace OpenForecourt.HostSimulator.UnitTests;

/// <summary>
/// The engine's decisions, tested with no sockets and no real waiting. Latency and silence
/// are values on the returned <see cref="HostDecision"/>, so timeout-driven behaviour is
/// verifiable in microseconds.
/// </summary>
public class HostEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 14, 30, 5, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static HostEngine NewEngine(HostRules? rules = null, HostLedger? ledger = null) =>
        new(rules ?? new HostRules(), ledger ?? new HostLedger(), new FixedClock(Now));

    private static Iso8583Message Financial(string pan, long amount, string stan) =>
        TerminalMessages.Financial(pan, amount, stan, Now);

    [Fact]
    public void Approves_below_the_threshold_and_issues_an_auth_code()
    {
        var engine = NewEngine(new HostRules { ApproveBelowMinor = 10_000 });

        var decision = engine.Handle(Financial("4111111111111111", 4500, "000001"));

        Assert.Equal("0210", decision.Response!.Mti);
        Assert.Equal(HostResponseCodes.Approved, decision.Response[Fields.ResponseCode]);
        Assert.False(string.IsNullOrEmpty(decision.Response[Fields.AuthorisationCode]));
    }

    [Fact]
    public void Declines_at_or_above_the_threshold_with_51_and_no_auth_code()
    {
        var engine = NewEngine(new HostRules { ApproveBelowMinor = 10_000 });

        var decision = engine.Handle(Financial("4111111111111111", 10_000, "000001"));

        Assert.Equal(HostResponseCodes.InsufficientFunds, decision.Response![Fields.ResponseCode]);
        Assert.Null(decision.Response[Fields.AuthorisationCode]);
    }

    [Fact]
    public void A_pan_rule_overrides_the_amount_rule()
    {
        var rules = new HostRules
        {
            ApproveBelowMinor = 10_000,
            DeclineByPan = new Dictionary<string, string> { ["5555555555554444"] = HostResponseCodes.ExpiredCard },
        };
        var engine = NewEngine(rules);

        // A trivial amount that the threshold would approve is still declined by the PAN rule.
        var decision = engine.Handle(Financial("5555555555554444", 100, "000001"));

        Assert.Equal(HostResponseCodes.ExpiredCard, decision.Response![Fields.ResponseCode]);
    }

    [Fact]
    public void The_response_never_carries_the_pan()
    {
        var decision = NewEngine().Handle(Financial("4111111111111111", 4500, "000001"));

        Assert.Null(decision.Response![Fields.Pan]);
        Assert.Null(decision.Response[Fields.IccData]);
    }

    [Fact]
    public void A_repeated_stan_from_the_same_terminal_is_a_duplicate()
    {
        var engine = NewEngine();
        var request = Financial("4111111111111111", 4500, "000001");

        engine.Handle(request);
        var second = engine.Handle(request);

        Assert.Equal(HostResponseCodes.DuplicateTransmission, second.Response![Fields.ResponseCode]);
    }

    [Fact]
    public void The_same_stan_from_a_different_terminal_is_not_a_duplicate()
    {
        var engine = NewEngine();
        engine.Handle(TerminalMessages.Financial("4111111111111111", 4500, "000001", Now));

        var otherTerminal = Iso8583Message.Create("0200")
            .Set(Fields.Pan, "4111111111111111")
            .Set(Fields.Amount, 4500L)
            .Set(Fields.Stan, "000001")
            .Set(Fields.TerminalId, "OPT99999")
            .Set(Fields.Currency, TerminalMessages.CurrencyGbp)
            .Build();

        var decision = engine.Handle(otherTerminal);

        Assert.Equal(HostResponseCodes.Approved, decision.Response![Fields.ResponseCode]);
    }

    [Fact]
    public void A_configured_no_response_pan_produces_silence()
    {
        var rules = new HostRules { NoResponsePans = ["378282246310005"] };
        var engine = NewEngine(rules);

        var decision = engine.Handle(Financial("378282246310005", 3000, "000001"));

        Assert.Null(decision.Response);
    }

    [Fact]
    public void Latency_is_returned_as_a_value_not_slept()
    {
        var rules = new HostRules
        {
            LatencyMs = 25,
            ExtraLatencyByPanMs = new Dictionary<string, int> { ["4000000000000101"] = 1500 },
        };
        var engine = NewEngine(rules);

        Assert.Equal(TimeSpan.FromMilliseconds(25), engine.Handle(Financial("4111111111111111", 4500, "000001")).Latency);
        Assert.Equal(TimeSpan.FromMilliseconds(1525), engine.Handle(Financial("4000000000000101", 4500, "000002")).Latency);
    }

    [Fact]
    public void An_invalid_amount_is_declined_13()
    {
        var engine = NewEngine();
        var zero = Iso8583Message.Create("0200")
            .Set(Fields.Amount, 0L)
            .Set(Fields.Stan, "000001")
            .Set(Fields.TerminalId, "OPT00001")
            .Build();

        Assert.Equal(HostResponseCodes.InvalidAmount, engine.Handle(zero).Response![Fields.ResponseCode]);
    }

    [Fact]
    public void An_unsupported_mti_is_declined_12()
    {
        var engine = NewEngine();
        var weird = Iso8583Message.Create("0500").Set(Fields.Stan, "000001").Build();

        var decision = engine.Handle(weird);

        Assert.Equal("0510", decision.Response!.Mti);
        Assert.Equal(HostResponseCodes.InvalidTransaction, decision.Response[Fields.ResponseCode]);
    }

    [Fact]
    public void A_reversal_of_an_approved_transaction_returns_the_money_and_is_idempotent()
    {
        var ledger = new HostLedger();
        var engine = NewEngine(ledger: ledger);
        var original = Financial("4111111111111111", 4500, "000001");
        engine.Handle(original);

        Assert.Equal(4500, ledger.OutstandingMinor);

        var reversal = TerminalMessages.Reversal(original, "000002", Now);
        var first = engine.Handle(reversal);

        Assert.Equal(HostResponseCodes.Approved, first.Response![Fields.ResponseCode]);
        Assert.Equal(0, ledger.OutstandingMinor);

        // A second reversal of the same original does nothing: 21 no action taken.
        var second = engine.Handle(TerminalMessages.Reversal(original, "000003", Now));
        Assert.Equal(HostResponseCodes.NoActionTaken, second.Response![Fields.ResponseCode]);
        Assert.Equal(0, ledger.OutstandingMinor);
    }

    [Fact]
    public void A_reversal_of_an_unknown_original_takes_no_action()
    {
        var engine = NewEngine();
        var original = Financial("4111111111111111", 4500, "000001");

        var decision = engine.Handle(TerminalMessages.Reversal(original, "000002", Now));

        Assert.Equal(HostResponseCodes.NoActionTaken, decision.Response![Fields.ResponseCode]);
    }

    [Fact]
    public void A_reversal_of_a_declined_transaction_takes_no_action()
    {
        var engine = NewEngine(new HostRules { ApproveBelowMinor = 1000 });
        var declined = Financial("4111111111111111", 5000, "000001");
        engine.Handle(declined);

        var decision = engine.Handle(TerminalMessages.Reversal(declined, "000002", Now));

        Assert.Equal(HostResponseCodes.NoActionTaken, decision.Response![Fields.ResponseCode]);
    }

    [Theory]
    [InlineData("301", HostResponseCodes.Approved)]
    [InlineData("001", HostResponseCodes.Approved)]
    [InlineData("999", HostResponseCodes.InvalidTransaction)]
    public void Network_management_echoes_with_a_response_code(string code, string expected)
    {
        var decision = NewEngine().Handle(TerminalMessages.NetworkManagement(code, "000001", Now));

        Assert.Equal("0810", decision.Response!.Mti);
        Assert.Equal(expected, decision.Response[Fields.ResponseCode]);
    }

    [Fact]
    public void Response_mti_is_request_mti_plus_ten()
    {
        Assert.Equal("0210", HostEngine.ResponseMti("0200"));
        Assert.Equal("0410", HostEngine.ResponseMti("0400"));
        Assert.Equal("0810", HostEngine.ResponseMti("0800"));
    }
}
