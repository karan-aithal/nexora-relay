using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Crypto.Tokenization;
using Xunit;

namespace OpenForecourt.Crypto.UnitTests;

public sealed class TokenizationTests
{
    private static readonly string[] TestPans =
        ["4111111111111111", "5555555555554444", "378282246310005", "4000000000000002"];

    [Fact]
    public async Task Token_is_deterministic_same_length_luhn_valid_and_keeps_last_four()
    {
        var vault = new FpeTokenVault();
        foreach (string panDigits in TestPans)
        {
            var pan = new Pan(panDigits);
            string token = await vault.TokenizeAsync(pan, CancellationToken.None);
            string again = await vault.TokenizeAsync(pan, CancellationToken.None);

            Assert.Equal(token, again);                       // deterministic within the vault
            Assert.Equal(panDigits.Length, token.Length);     // format preserving
            Assert.All(token, c => Assert.InRange(c, '0', '9'));
            Assert.True(Luhn.IsValid(token));                 // passes Luhn
            Assert.Equal(panDigits[^4..], token[^4..]);       // last four preserved for receipts
            Assert.NotEqual(panDigits, token);                // the middle actually changed
        }
    }

    [Fact]
    public async Task Distinct_pans_get_distinct_tokens()
    {
        var vault = new FpeTokenVault();
        var tokens = new List<string>();
        foreach (string panDigits in TestPans)
        {
            tokens.Add(await vault.TokenizeAsync(new Pan(panDigits), CancellationToken.None));
        }

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Token_is_irreversible_without_the_issuing_vault()
    {
        var issuing = new FpeTokenVault();
        string token = await issuing.TokenizeAsync(new Pan("4111111111111111"), CancellationToken.None);

        // The issuing vault can recover the PAN...
        var here = await issuing.DetokenizeAsync(token, CancellationToken.None);
        Assert.True(here.IsSuccess);
        Assert.Equal("4111111111111111", here.Value.Reveal());

        // ...but a different vault (different key, empty map) cannot.
        var other = new FpeTokenVault();
        var elsewhere = await other.DetokenizeAsync(token, CancellationToken.None);
        Assert.True(elsewhere.IsError);
        Assert.Equal(TokenVaultError.UnknownToken, elsewhere.Error);
    }
}
