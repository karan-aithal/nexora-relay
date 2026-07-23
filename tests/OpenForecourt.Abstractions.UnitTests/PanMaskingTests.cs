using OpenForecourt.Abstractions.Domain;
using Xunit;

namespace OpenForecourt.Abstractions.UnitTests;

/// <summary>
/// Enforces CLAUDE.md section 7.3: PAN masking is guaranteed by a test, not by discipline.
/// The masked form must never expose more than the first 6 and last 4 digits.
/// </summary>
public class PanMaskingTests
{
    [Theory]
    [InlineData("4111111111111111")] // 16-digit Visa test PAN
    [InlineData("5555555555554444")] // 16-digit Mastercard test PAN
    [InlineData("378282246310005")]  // 15-digit Amex test PAN
    [InlineData("6011000990139424")] // 16-digit Discover test PAN
    [InlineData("30569309025904")]   // 14-digit Diners test PAN
    public void ToString_never_exposes_more_than_first_6_and_last_4(string digits)
    {
        var pan = new Pan(digits);
        string masked = pan.ToString();

        // Count leading digits before the first mask character.
        int leading = masked.TakeWhile(char.IsDigit).Count();
        // Count trailing digits after the last mask character.
        int trailing = masked.Reverse().TakeWhile(char.IsDigit).Count();

        Assert.True(leading <= 6, $"Exposed {leading} leading digits (max 6): {masked}");
        Assert.True(trailing <= 4, $"Exposed {trailing} trailing digits (max 4): {masked}");
        Assert.Equal(digits.Length, masked.Length);
    }

    [Fact]
    public void ToString_masks_the_middle_of_a_standard_pan()
    {
        var pan = new Pan("4111111111111111");
        Assert.Equal("411111******1111", pan.ToString());
    }

    [Fact]
    public void Reveal_returns_the_raw_digits()
    {
        var pan = new Pan("4111111111111111");
        Assert.Equal("4111111111111111", pan.Reveal());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4111 1111")]
    [InlineData("41a1")]
    public void Constructor_rejects_non_digit_input(string? bad)
    {
        Assert.Throws<ArgumentException>(() => new Pan(bad!));
    }
}
