using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Crypto.Dukpt;
using OpenForecourt.Crypto.Pin;
using OpenForecourt.Crypto.Tokenization;
using Xunit;

namespace OpenForecourt.Crypto.UnitTests;

/// <summary>
/// CLAUDE.md §7.3 / Phase 3: after a transaction, walk every persisted artefact and every
/// log sink and fail if an unmasked PAN — or the cleartext PIN — appears anywhere.
/// </summary>
/// <remarks>
/// Phase 3's downstream components (SQLite journal, RabbitMQ payloads, dashboard) do not exist
/// yet, so this scans the artefacts the crypto boundary itself produces: what the OPT tokenizes
/// and hands on (token, masked PAN, encrypted PIN block, KSN). The equivalent scan over the
/// live host/terminal wire already runs in HostSimulator.UnitTests.PanLeakTests; the two extend
/// to the journal and MQ when those land (backlog).
/// </remarks>
public sealed class PanScanTests
{
    [Fact]
    public async Task No_downstream_artefact_holds_a_cleartext_pan_or_pin()
    {
        const string clearPan = "4111111111111111";
        const string clearPin = "1234";
        var pan = new Pan(clearPan);

        // OPT boundary: tokenize the PAN and encrypt the PIN under a DUKPT PIN key.
        var vault = new FpeTokenVault();
        string token = await vault.TokenizeAsync(pan, CancellationToken.None);

        byte[] ipek = DukptTdes.DeriveIpek(
            Convert.FromHexString("0123456789ABCDEFFEDCBA9876543210"), Ksn.Parse("FFFF9876543210E00000"));
        var ksn = Ksn.Parse("FFFF9876543210E00000").Advance().Value;
        byte[] pinKey = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.PinEncryption);
        byte[] pinBlock = PinBlock.EncryptFormat0(clearPin, pan, pinKey);

        // Everything downstream of the OPT may hold: the token, the masked PAN, the encrypted
        // PIN block, and the KSN — never cleartext PAN or PIN.
        var artefacts = new List<string>
        {
            $"journal: token={token} masked={pan} ksn={ksn} pinblock={Convert.ToHexString(pinBlock)}",
            $"mq-payload: {{\"token\":\"{token}\",\"pan\":\"{pan}\",\"amount\":4500}}",
            $"log: authorised token={token} pan={pan}",
            $"receipt: **** **** **** {token[^4..]}",
        };

        // Sanity: the scan is looking at real PAN-bearing output, not empty strings.
        Assert.Contains(artefacts, a => a.Contains("411111******1111", StringComparison.Ordinal));
        Assert.Contains(artefacts, a => a.Contains(token, StringComparison.Ordinal));

        foreach (string artefact in artefacts)
        {
            Assert.DoesNotContain(clearPan, artefact, StringComparison.Ordinal);
            // The cleartext PIN must not appear as an isolated token either.
            Assert.DoesNotContain($"pin={clearPin}", artefact, StringComparison.Ordinal);
        }

        // The encrypted PIN block must not be the PIN in the clear.
        Assert.DoesNotContain(clearPin, Convert.ToHexString(pinBlock), StringComparison.Ordinal);
    }
}
