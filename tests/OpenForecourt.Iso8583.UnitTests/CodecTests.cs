using Xunit;

namespace OpenForecourt.Iso8583.UnitTests;

/// <summary>
/// Decoding is the untrusted direction — every byte comes off a socket. Nothing here may
/// throw; a malformed message must come back as a diagnostic saying what was wrong and where.
/// </summary>
public class CodecTests
{
    private readonly Iso8583Codec _codec = new();

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    [Fact]
    public void Encodes_fields_in_ascending_order_regardless_of_the_order_they_were_set()
    {
        var a = Iso8583Message.Create("0200").Set(Fields.Currency, "826").Set(Fields.Stan, "000123").Build();
        var b = Iso8583Message.Create("0200").Set(Fields.Stan, "000123").Set(Fields.Currency, "826").Build();

        Assert.Equal(_codec.Encode(a), _codec.Encode(b));
    }

    [Fact]
    public void Fixed_numeric_fields_are_stored_zero_filled_so_a_round_trip_is_stable()
    {
        var message = Iso8583Message.Create("0200").Set(Fields.Amount, 5000L).Build();

        Assert.Equal("000000005000", message[Fields.Amount]);

        var decoded = _codec.TryDecode(_codec.Encode(message));
        Assert.True(decoded.IsSuccess);
        Assert.Equal("000000005000", decoded.Value[Fields.Amount]);
    }

    [Fact]
    public void Fixed_alphanumeric_fields_are_space_filled()
    {
        var message = Iso8583Message.Create("0200").Set(Fields.TerminalId, "OPT1").Build();

        Assert.Equal("OPT1    ", message[Fields.TerminalId]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("30")]
    [InlineData("30323030")]                      // MTI only, no bitmap
    [InlineData("303230307220")]                  // partial bitmap
    [InlineData("3032303080200000000000000000")]  // bit 1 set, secondary bitmap truncated
    public void Truncated_input_is_reported_not_thrown(string hex)
    {
        var result = _codec.TryDecode(Hex(hex));

        Assert.True(result.IsError);
        Assert.Contains("truncated", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_non_numeric_mti_is_rejected()
    {
        var result = _codec.TryDecode(Hex("4142434400000000000000000000"));

        Assert.True(result.IsError);
        Assert.Contains("MTI", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_the_dialect_does_not_define_is_rejected_with_its_number()
    {
        // 0x08 in byte 0 = bit 5, and OFC-87 has no field 5.
        var result = _codec.TryDecode(Hex("30323030" + "0800000000000000" + "3132333435"));

        Assert.True(result.IsError);
        Assert.Equal(5, result.Error.Field);
        Assert.Contains("does not define", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_field_claiming_more_than_its_maximum_is_rejected_before_reading_it()
    {
        // Field 2 with an LLVAR length of 99, above its maximum of 19.
        var result = _codec.TryDecode(Hex("30323030" + "4000000000000000" + "3939" + "31323334"));

        Assert.True(result.IsError);
        Assert.Equal(2, result.Error.Field);
        Assert.Contains("exceeds", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_length_prefix_is_rejected()
    {
        var result = _codec.TryDecode(Hex("30323030" + "4000000000000000" + "4142" + "31323334"));

        Assert.True(result.IsError);
        Assert.Contains("length prefix", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_numeric_field_carrying_letters_is_rejected()
    {
        // Field 3 (n 6) containing "ABCDEF".
        var result = _codec.TryDecode(Hex("30323030" + "2000000000000000" + "414243444546"));

        Assert.True(result.IsError);
        Assert.Equal(3, result.Error.Field);
        Assert.Contains("digits", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Trailing_bytes_after_the_last_field_are_rejected()
    {
        var message = Iso8583Message.Create("0200").Set(Fields.Stan, "000123").Build();
        byte[] encoded = [.. _codec.Encode(message), 0x41];

        var result = _codec.TryDecode(encoded);

        Assert.True(result.IsError);
        Assert.Contains("trailing", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bitmap_that_announces_a_secondary_containing_nothing_still_decodes()
    {
        var result = _codec.TryDecode(Hex("30323030" + "8000000000000000" + "0000000000000000"));

        Assert.True(result.IsSuccess);
        Assert.Equal("0200", result.Value.Mti);
        Assert.Empty(result.Value.PresentFields);
    }

    [Fact]
    public void An_empty_variable_field_is_legal()
    {
        var result = _codec.TryDecode(Hex("30323030" + "4000000000000000" + "3030"));

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Value[Fields.Pan]);
    }

    [Fact]
    public void Building_a_message_with_an_unencodable_value_is_a_programming_error()
    {
        var builder = Iso8583Message.Create("0200");

        Assert.Throws<ArgumentException>(() => builder.Set(Fields.Amount, "12A4"));
        Assert.Throws<ArgumentException>(() => builder.Set(Fields.Pan, new string('4', 20)));
        Assert.Throws<ArgumentException>(() => builder.Set(Fields.IccData, "9F27018"));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Set(999, "x"));
        Assert.Throws<ArgumentException>(() => Iso8583Message.Create("020"));
    }
}
