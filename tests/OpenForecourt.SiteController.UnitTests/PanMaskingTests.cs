using Serilog;
using OpenForecourt.SiteController.Logging;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

public sealed class PanMaskingTests
{
    // Reserved test PANs (CLAUDE.md section 7.1). Never a real card number.
    private const string Visa16 = "4111111111111111";
    private const string Amex15 = "378282246310005";

    [Theory]
    [InlineData("4111111111111111", "411111******1111")]
    [InlineData("378282246310005", "378282*****0005")]
    [InlineData("pan=4111111111111111 done", "pan=411111******1111 done")]
    public void Mask_replaces_pan_with_first6_last4(string input, string expected) =>
        Assert.Equal(expected, PanMasker.Mask(input));

    [Fact]
    public void Mask_leaves_short_digit_runs_alone()
    {
        Assert.Equal("order 123456789012", PanMasker.Mask("order 123456789012")); // 12 digits, not a PAN
        Assert.Equal("no digits here", PanMasker.Mask("no digits here"));
    }

    // CLAUDE.md section 7.3: masking is enforced by a test, not by discipline. Whatever a caller
    // logs, the bytes leaving the sink must never contain an unmasked PAN.
    [Fact]
    public void Sink_masks_pan_at_every_level_regardless_of_caller()
    {
        using var writer = new StringWriter();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new PanMaskingSink(writer))
            .CreateLogger();

        // A careless caller logs raw PANs at several levels and as a structured property.
        logger.Verbose("card {Pan} presented", Visa16);
        logger.Information("authorising " + Amex15);
        logger.Error("declined for {Pan}", Visa16);

        string output = writer.ToString();
        Assert.DoesNotContain(Visa16, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Amex15, output, StringComparison.Ordinal);
        Assert.Contains("411111******1111", output, StringComparison.Ordinal);
        Assert.Contains("378282*****0005", output, StringComparison.Ordinal);
    }
}
