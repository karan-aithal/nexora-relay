using OpenForecourt.Emv.BerTlv;
using Xunit;

namespace OpenForecourt.Emv.UnitTests;

public sealed class BerTlvTests
{
    // Built by construction rather than from a collection expression: the expression lowers to
    // an empty-array allocation plus Add calls, which CA1825 flags on newer analyzer versions.
    public static TheoryData<string> GoldenFiles => new()
    {
        "ppse-fci.hex",
        "pse-fci.hex",
        "generate-ac.hex",
    };

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Golden_files_round_trip_byte_identically(string file)
    {
        byte[] original = Convert.FromHexString(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", file)).Trim());

        var parsed = BerTlvCodec.TryParse(original);
        Assert.True(parsed.IsSuccess, parsed.IsError ? parsed.Error.ToString() : "");

        // Re-emitting every top-level node must reproduce the input exactly.
        byte[] rewritten = parsed.Value.SelectMany(n => n.ToBytes()).ToArray();
        Assert.Equal(Convert.ToHexString(original), Convert.ToHexString(rewritten));
    }

    [Fact]
    public void Find_navigates_into_constructed_children()
    {
        // From the PPSE FCI, the AID (tag 4F) is nested three levels down: 6F > A5 > BF0C > 61 > 4F.
        byte[] fci = Convert.FromHexString(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "ppse-fci.hex")).Trim());
        var root = BerTlvCodec.TryParse(fci).Value.Single();

        var aid = root.Find(Tag.Parse("4F"));
        Assert.NotNull(aid);
        Assert.Equal("A0000000041010", Convert.ToHexString(aid!.Value.Span));

        // A two-byte tag (BF0C) is found and is constructed.
        var idd = root.Find(Tag.Parse("BF0C"));
        Assert.NotNull(idd);
        Assert.True(idd!.Tag.IsConstructed);
    }

    [Fact]
    public void Two_byte_tag_reads_back_its_bytes()
    {
        Assert.True(Tag.TryRead(Convert.FromHexString("9F26"), out var tag, out int consumed));
        Assert.Equal(2, consumed);
        Assert.Equal("9F26", tag.ToString());
        Assert.False(tag.IsConstructed);
        Assert.Equal("9F26", Convert.ToHexString(tag.Bytes));
    }

    [Fact]
    public void Constructed_bit_is_read_from_first_tag_byte()
    {
        Assert.True(Tag.TryRead([0x6F], out var fci, out _));
        Assert.True(fci.IsConstructed);
        Assert.True(Tag.TryRead([0x5A], out var pan, out _));
        Assert.False(pan.IsConstructed);
    }

    [Theory]
    [InlineData("9F", "truncated multi-byte tag")]
    [InlineData("5A05411111", "length runs past the buffer")]
    [InlineData("3080", "indefinite length form")]
    [InlineData("5A85000000000141", "oversized length-of-length")]
    [InlineData("6F", "truncated after tag, no length")]
    public void Malformed_input_returns_error_without_throwing(string hex, string _)
    {
        var result = BerTlvCodec.TryParse(Convert.FromHexString(hex));
        Assert.True(result.IsError);
    }

    [Fact]
    public void Deeply_nested_input_is_rejected_not_stack_overflowed()
    {
        // Build 40 levels of constructed tag 0x6F, each wrapping the next. Lengths are fixed
        // up from the innermost outward so the bytes are well-formed but pathologically deep.
        byte[] node = [0x5A, 0x01, 0x00];
        for (int i = 0; i < 40; i++)
        {
            node = [0x6F, (byte)node.Length, .. node];
        }

        var result = BerTlvCodec.TryParse(node);
        Assert.True(result.IsError); // guarded by MaxDepth, no crash
    }

    [Fact]
    public void Empty_input_parses_to_no_nodes()
    {
        var result = BerTlvCodec.TryParse([]);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }
}
