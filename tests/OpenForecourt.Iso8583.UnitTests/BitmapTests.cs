using Xunit;

namespace OpenForecourt.Iso8583.UnitTests;

/// <summary>
/// The bitmap is where hand-written ISO 8583 codecs usually go wrong: bit 1 is the most
/// significant bit of byte 0, bit 1 is not a field, and the secondary bitmap exists only
/// when it must.
/// </summary>
public class BitmapTests
{
    [Fact]
    public void No_fields_above_64_means_no_secondary_bitmap()
    {
        var bitmap = Bitmap.ForFields([2, 3, 4, 11, 41, 49]);

        Assert.False(bitmap.HasSecondary);
        Assert.Equal(8, bitmap.EncodedLength);
        Assert.False(bitmap.IsSet(1));
        // 0x70 = 0111 0000 -> fields 2,3,4 ; 0x20 -> field 11 ; 0x80 -> field 41 ; 0x80 -> field 49.
        Assert.Equal("7020000000808000", bitmap.ToString());
    }

    [Fact]
    public void A_field_above_64_sets_bit_1_and_emits_a_secondary_bitmap()
    {
        var bitmap = Bitmap.ForFields([3, 4, 11, 41, 90]);

        Assert.True(bitmap.HasSecondary);
        Assert.Equal(16, bitmap.EncodedLength);
        Assert.True(bitmap.IsSet(1));
        // B0 = 1011 0000: bit 1 (secondary follows) plus fields 3 and 4.
        Assert.Equal("B020000000800000" + "0000004000000000", bitmap.ToString());
    }

    [Fact]
    public void Field_128_is_the_last_bit_of_the_secondary_bitmap()
    {
        var bitmap = Bitmap.ForFields([128]);

        Assert.True(bitmap.IsSet(128));
        Assert.Equal("8000000000000000" + "0000000000000001", bitmap.ToString());
        Assert.Equal([128], bitmap.PresentFields());
    }

    [Fact]
    public void Field_2_is_the_second_most_significant_bit_of_byte_0()
    {
        Assert.Equal("4000000000000000", Bitmap.ForFields([2]).ToString());
    }

    [Fact]
    public void Bit_1_is_never_reported_as_a_present_field()
    {
        var bitmap = Bitmap.ForFields([2, 70]);

        Assert.True(bitmap.IsSet(1));
        Assert.Equal([2, 70], bitmap.PresentFields());
    }

    [Fact]
    public void Setting_field_1_directly_is_rejected_because_it_is_not_a_field()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Bitmap.ForFields([1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bitmap.ForFields([129]));
    }

    [Fact]
    public void Round_trips_through_bytes()
    {
        var original = Bitmap.ForFields([2, 3, 4, 7, 11, 41, 49, 55, 70, 90, 128]);
        Span<byte> buffer = stackalloc byte[16];
        int written = original.WriteTo(buffer);

        Assert.True(Bitmap.TryRead(buffer[..written], out var decoded, out int read));

        Assert.Equal(written, read);
        Assert.Equal(original, decoded);
        Assert.Equal(original.PresentFields(), decoded.PresentFields());
    }

    [Fact]
    public void A_bitmap_with_only_bit_1_set_reads_16_bytes_and_reports_no_fields()
    {
        // Legal but odd: the sender announced a secondary bitmap and then set nothing in it.
        byte[] wire = [0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.True(Bitmap.TryRead(wire, out var bitmap, out int read));

        Assert.Equal(16, read);
        Assert.True(bitmap.IsSet(1));
        Assert.Empty(bitmap.PresentFields());
    }

    [Fact]
    public void A_truncated_bitmap_is_reported_not_guessed()
    {
        Assert.False(Bitmap.TryRead(new byte[7], out _, out _));

        // Bit 1 promises a secondary bitmap that is not there.
        byte[] promisedButAbsent = [0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(Bitmap.TryRead(promisedButAbsent, out _, out _));
    }
}
