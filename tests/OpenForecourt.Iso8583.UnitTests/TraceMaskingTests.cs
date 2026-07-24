using Xunit;

namespace OpenForecourt.Iso8583.UnitTests;

/// <summary>
/// The field-by-field trace (<see cref="Iso8583Message.ToString"/>) is the diagnostic you
/// live in for weeks, so it is also the most likely place a clear PAN leaks into a log. The
/// masking is enforced here, not by discipline (CLAUDE.md section 7).
/// </summary>
public class TraceMaskingTests
{
    [Fact]
    public void The_trace_masks_the_pan_to_first_six_and_last_four()
    {
        var message = Iso8583Message.Create("0200")
            .Set(Fields.Pan, "4111111111111111")
            .Set(Fields.Amount, 4500L)
            .Build();

        string trace = message.ToString();

        Assert.Contains("411111******1111", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void The_trace_renders_the_field_number_name_and_descriptor()
    {
        var trace = Iso8583Message.Create("0200")
            .Set(Fields.Stan, "000123")
            .Build()
            .ToString();

        Assert.Contains("MTI 0200", trace, StringComparison.Ordinal);
        Assert.Contains("Financial request", trace, StringComparison.Ordinal);
        Assert.Contains("11", trace, StringComparison.Ordinal);
        Assert.Contains("System trace audit number", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_mti_is_labelled_not_hidden()
    {
        Assert.Contains("Unknown message type", Iso8583Message.Create("0999").Build().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Non_sensitive_fields_are_shown_in_full()
    {
        var trace = Iso8583Message.Create("0210")
            .Set(Fields.AuthorisationCode, "A00001")
            .Build()
            .ToString();

        Assert.Contains("A00001", trace, StringComparison.Ordinal);
    }
}
