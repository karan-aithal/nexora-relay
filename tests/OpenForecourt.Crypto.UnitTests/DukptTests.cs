using OpenForecourt.Crypto.Dukpt;
using OpenForecourt.Crypto.Pin;
using OpenForecourt.Abstractions.Domain;
using Xunit;

namespace OpenForecourt.Crypto.UnitTests;

public sealed class DukptTests
{
    // The canonical ANSI X9.24-1 worked example — the most widely reproduced DUKPT vector.
    private const string Bdk = "0123456789ABCDEFFEDCBA9876543210";
    private const string InitialKsn = "FFFF9876543210E00000";
    private const string ExpectedIpek = "6AC292FAA1315B4D858AB3A3D7D5933A";

    [Fact]
    public void Ipek_matches_the_published_x924_vector()
    {
        byte[] ipek = DukptTdes.DeriveIpek(Convert.FromHexString(Bdk), Ksn.Parse(InitialKsn));
        Assert.Equal(ExpectedIpek, Convert.ToHexString(ipek));
    }

    [Fact]
    public void Each_counter_advance_yields_a_distinct_pin_key()
    {
        byte[] ipek = DukptTdes.DeriveIpek(Convert.FromHexString(Bdk), Ksn.Parse(InitialKsn));
        var ksn = Ksn.Parse(InitialKsn);

        var keys = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            ksn = ksn.Advance().Value; // counters 1, 2, 3
            byte[] key = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.PinEncryption);
            keys.Add(Convert.ToHexString(key));
        }

        Assert.Equal(3u, ksn.Counter);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        // Derivation is deterministic: re-deriving counter 3 reproduces the same key.
        byte[] again = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.PinEncryption);
        Assert.Equal(keys[^1], Convert.ToHexString(again));
    }

    [Fact]
    public void Terminal_and_host_derive_the_same_pin_key_and_round_trip_a_pin_block()
    {
        // Terminal side: injected with the IPEK, advances its KSN, encrypts a PIN block.
        byte[] ipek = DukptTdes.DeriveIpek(Convert.FromHexString(Bdk), Ksn.Parse(InitialKsn));
        var ksn = Ksn.Parse(InitialKsn).Advance().Value;
        var pan = new Pan("4111111111111111");
        byte[] terminalKey = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.PinEncryption);
        byte[] cipher = PinBlock.EncryptFormat0("1234", pan, terminalKey);

        // Host side: holds only the BDK, re-derives the IPEK then the same transaction key
        // from the KSN carried on the wire, and decrypts back to the original PIN.
        byte[] hostIpek = DukptTdes.DeriveIpek(Convert.FromHexString(Bdk), Ksn.Parse(InitialKsn));
        byte[] hostKey = DukptTdes.DeriveTransactionKey(hostIpek, ksn, DukptKeyType.PinEncryption);
        Assert.Equal(Convert.ToHexString(terminalKey), Convert.ToHexString(hostKey));

        var recovered = PinBlock.DecryptFormat0(cipher, pan, hostKey);
        Assert.True(recovered.IsSuccess);
        Assert.Equal("1234", recovered.Value);
    }

    [Fact]
    public void Data_key_round_trips_a_block_terminal_to_host()
    {
        byte[] ipek = DukptTdes.DeriveIpek(Convert.FromHexString(Bdk), Ksn.Parse(InitialKsn));
        var ksn = Ksn.Parse(InitialKsn).Advance().Value;

        byte[] terminalKey = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.DataEncryption);
        byte[] hostKey = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.DataEncryption);
        Assert.Equal(Convert.ToHexString(terminalKey), Convert.ToHexString(hostKey));

        byte[] plaintext = Convert.FromHexString("0011223344556677");
        byte[] cipher = DukptTdes.EncryptBlock(terminalKey, plaintext);
        Assert.NotEqual(Convert.ToHexString(plaintext), Convert.ToHexString(cipher));
        byte[] back = DukptTdes.DecryptBlock(hostKey, cipher);
        Assert.Equal(Convert.ToHexString(plaintext), Convert.ToHexString(back));
    }

    [Fact]
    public void Pin_and_data_variants_differ_for_the_same_ksn()
    {
        byte[] ipek = DukptTdes.DeriveIpek(Convert.FromHexString(Bdk), Ksn.Parse(InitialKsn));
        var ksn = Ksn.Parse(InitialKsn).Advance().Value;
        byte[] pin = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.PinEncryption);
        byte[] data = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.DataEncryption);
        Assert.NotEqual(Convert.ToHexString(pin), Convert.ToHexString(data));
    }

    [Fact]
    public void Counter_advances_and_reports_exhaustion()
    {
        // Start one below the maximum: one advance succeeds, the next is refused.
        var almost = new Ksn(Convert.FromHexString("FFFF9876543210FFFFFE"));
        Assert.Equal(Ksn.MaxCounter - 1, almost.Counter);

        var advanced = almost.Advance();
        Assert.True(advanced.IsSuccess);
        Assert.Equal(Ksn.MaxCounter, advanced.Value.Counter);

        var exhausted = advanced.Value.Advance();
        Assert.True(exhausted.IsError);
        Assert.Equal(KsnError.CounterExhausted, exhausted.Error);
    }
}
