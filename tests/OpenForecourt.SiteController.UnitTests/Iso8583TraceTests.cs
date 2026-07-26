using OpenForecourt.Iso8583;
using OpenForecourt.SiteController.Tracing;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

public sealed class Iso8583TraceTests
{
    private static Iso8583Message Financial() => Iso8583Message.Create("0200")
        .Set(Fields.Pan, "4111111111111111")
        .Set(Fields.ProcessingCode, "000000")
        .Set(Fields.Amount, 2500)
        .Set(Fields.Stan, "000123")
        .Set(Fields.TerminalId, "OPT00001")
        .Set(Fields.Currency, "826")
        .Build();

    // CLAUDE.md section 7.3: the trace shown in the dashboard must never widen what a log may
    // show. Masking is driven by the dialect's Sensitive flag, so this asserts the wiring.
    [Fact]
    public void Sensitive_fields_are_masked_in_the_described_trace()
    {
        var described = Iso8583Trace.Describe(Financial());

        var pan = Assert.Single(described, f => f.Name.StartsWith("DE 002", StringComparison.Ordinal));
        Assert.Equal("411111******1111", pan.Value);
        Assert.DoesNotContain(described, f => f.Value.Contains("4111111111111111", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_sensitive_fields_are_shown_verbatim_with_their_dialect_names()
    {
        var described = Iso8583Trace.Describe(Financial());

        var amount = Assert.Single(described, f => f.Name.StartsWith("DE 004", StringComparison.Ordinal));
        Assert.Contains("Amount", amount.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("000000002500", amount.Value);

        var terminal = Assert.Single(described, f => f.Name.StartsWith("DE 041", StringComparison.Ordinal));
        Assert.Equal("OPT00001", terminal.Value);
    }

    [Fact]
    public void The_trace_leads_with_the_mti_and_bitmap()
    {
        var described = Iso8583Trace.Describe(Financial());

        Assert.Equal("MTI", described[0].Name);
        Assert.Contains("0200", described[0].Value, StringComparison.Ordinal);
        Assert.Equal("Bitmap", described[1].Name);
    }

    [Fact]
    public void The_label_names_the_message_type()
    {
        Assert.Contains("0200", Iso8583Trace.Label(Financial()), StringComparison.Ordinal);
    }
}
