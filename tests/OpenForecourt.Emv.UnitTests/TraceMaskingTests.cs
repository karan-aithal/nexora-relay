using OpenForecourt.Emv.BerTlv;
using Xunit;

namespace OpenForecourt.Emv.UnitTests;

public sealed class TraceMaskingTests
{
    [Fact]
    public void Decoded_trace_masks_the_pan_in_tag_5A()
    {
        // 5A08 5555555555554444
        var nodes = BerTlvCodec.TryParse(Convert.FromHexString("5A085555555555554444")).Value;
        string decoded = EmvTags.Describe(nodes);

        Assert.DoesNotContain("5555555555554444", decoded);
        Assert.Contains("555555******4444", decoded);
    }

    [Fact]
    public void Decoded_trace_masks_the_pan_in_track2_tag_57()
    {
        // 57: PAN then 'D' separator then expiry/discretionary.
        var nodes = BerTlvCodec.TryParse(Convert.FromHexString("571155555555555544 44D30122010000000000".Replace(" ", ""))).Value;
        string decoded = EmvTags.Describe(nodes);

        Assert.DoesNotContain("5555555555554444", decoded);
        Assert.Contains("555555******4444", decoded);
    }

    [Fact]
    public void Masking_can_be_disabled_for_in_boundary_diagnostics()
    {
        var nodes = BerTlvCodec.TryParse(Convert.FromHexString("5A085555555555554444")).Value;
        Assert.Contains("5555555555554444", EmvTags.Describe(nodes, maskPan: false));
    }
}
