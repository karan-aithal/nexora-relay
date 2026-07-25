using OpenForecourt.Crypto.Tokenization;
using Xunit;

namespace OpenForecourt.Crypto.UnitTests;

public sealed class LuhnTests
{
    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("5555555555554444")]
    [InlineData("378282246310005")]
    [InlineData("4000000000000002")]
    public void Reserved_test_pans_are_luhn_valid(string pan) => Assert.True(Luhn.IsValid(pan));

    [Fact]
    public void A_single_digit_error_fails_luhn()
    {
        // Flip the second-to-last digit of a valid PAN.
        Assert.False(Luhn.IsValid("4111111111111121"));
    }
}
