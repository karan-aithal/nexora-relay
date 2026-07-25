using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Crypto.Pin;
using Xunit;

namespace OpenForecourt.Crypto.UnitTests;

public sealed class PinBlockTests
{
    [Fact]
    public void Format0_clear_block_matches_the_known_construction()
    {
        // PIN field 041234FFFFFFFFFF XOR PAN field 0000111111111111 = 041225EEEEEEEEEE.
        byte[] clear = PinBlock.EncodeFormat0("1234", new Pan("4111111111111111"));
        Assert.Equal("041225EEEEEEEEEE", Convert.ToHexString(clear));
    }

    [Fact]
    public void Format0_round_trips_under_a_tdes_key()
    {
        byte[] key = Convert.FromHexString("0123456789ABCDEFFEDCBA9876543210");
        var pan = new Pan("5555555555554444");
        byte[] cipher = PinBlock.EncryptFormat0("8675", pan, key);
        Assert.Equal(8, cipher.Length);

        var recovered = PinBlock.DecryptFormat0(cipher, pan, key);
        Assert.True(recovered.IsSuccess);
        Assert.Equal("8675", recovered.Value);
    }

    [Fact]
    public void Format0_wrong_pan_does_not_recover_the_pin()
    {
        byte[] key = Convert.FromHexString("0123456789ABCDEFFEDCBA9876543210");
        byte[] cipher = PinBlock.EncryptFormat0("1234", new Pan("4111111111111111"), key);

        // Decrypting against a different PAN reconstructs a different PIN field; either the
        // format nibble or the digits will not match the original PIN.
        var wrong = PinBlock.DecryptFormat0(cipher, new Pan("5555555555554444"), key);
        Assert.True(wrong.IsError || wrong.Value != "1234");
    }

    [Fact]
    public void Format4_round_trips_under_an_aes_key()
    {
        byte[] aesKey = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var pan = new Pan("4111111111111111");
        byte[] cipher = PinBlock.EncryptFormat4("123456", pan, aesKey);
        Assert.Equal(16, cipher.Length);

        var recovered = PinBlock.DecryptFormat4(cipher, pan, aesKey);
        Assert.True(recovered.IsSuccess);
        Assert.Equal("123456", recovered.Value);
    }

    [Fact]
    public void Format4_random_fill_makes_ciphertext_non_deterministic_but_decodable()
    {
        byte[] aesKey = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var pan = new Pan("4111111111111111");
        byte[] a = PinBlock.EncryptFormat4("1234", pan, aesKey);
        byte[] b = PinBlock.EncryptFormat4("1234", pan, aesKey);
        Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b)); // random fill differs
        Assert.Equal("1234", PinBlock.DecryptFormat4(a, pan, aesKey).Value);
        Assert.Equal("1234", PinBlock.DecryptFormat4(b, pan, aesKey).Value);
    }
}
