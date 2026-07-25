using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Crypto.Dukpt;
using OpenForecourt.Crypto.Pin;
using OpenForecourt.Crypto.Tokenization;

// Phase 3 demo: DUKPT key derivation, a KSN advancing across three transactions with a
// different derived key each time, a PIN block encrypted and decrypted through the derived
// key, and a transaction record showing the token in place of the PAN. No hardware, no
// network; the BDK is the published X9.24 test key loaded from tests/testdata (CLAUDE.md §7).

byte[] bdk = LoadTestBdk();
var initialKsn = Ksn.Parse("FFFF9876543210E00000");
byte[] ipek = DukptTdes.DeriveIpek(bdk, initialKsn);
var pan = new Pan("4111111111111111");
const string pin = "1234";

Console.WriteLine(new string('=', 70));
Console.WriteLine(" OpenForecourt — Phase 3 demo (DUKPT + PIN blocks + tokenization)");
Console.WriteLine(new string('=', 70));
Console.WriteLine();
Console.WriteLine("Test keys and test PANs only (CLAUDE.md §7). The BDK never leaves the host;");
Console.WriteLine("the terminal is injected with the IPEK and derives a fresh key per transaction.");
Console.WriteLine();
Console.WriteLine($"BDK (test)   : {Convert.ToHexString(bdk)}");
Console.WriteLine($"Initial KSN  : {initialKsn}");
Console.WriteLine($"IPEK         : {Convert.ToHexString(ipek)}   <- injected into the OPT");
Console.WriteLine();

Console.WriteLine("Three transactions — the KSN counter advances and the derived PIN key changes:");
Console.WriteLine();

var ksn = initialKsn;
for (int i = 1; i <= 3; i++)
{
    ksn = ksn.Advance().Value;

    // Terminal side: derive the per-transaction PIN key and encrypt the PIN block.
    byte[] terminalKey = DukptTdes.DeriveTransactionKey(ipek, ksn, DukptKeyType.PinEncryption);
    byte[] pinCipher = PinBlock.EncryptFormat0(pin, pan, terminalKey);

    // Host side: from the BDK, re-derive the same key from the KSN on the wire and decrypt.
    byte[] hostKey = DukptTdes.DeriveTransactionKey(
        DukptTdes.DeriveIpek(bdk, initialKsn), ksn, DukptKeyType.PinEncryption);
    var recovered = PinBlock.DecryptFormat0(pinCipher, pan, hostKey);

    Console.WriteLine($"  txn {i}: KSN {ksn}  (counter {ksn.Counter})");
    Console.WriteLine($"         derived PIN key : {Convert.ToHexString(terminalKey)}");
    Console.WriteLine($"         PIN block cipher: {Convert.ToHexString(pinCipher)}");
    Console.WriteLine($"         host re-derived : {Convert.ToHexString(hostKey)}  (match: {Convert.ToHexString(terminalKey) == Convert.ToHexString(hostKey)})");
    Console.WriteLine($"         host decrypts PIN block -> PIN {(recovered.IsSuccess ? "recovered OK" : "FAILED")}");
    Console.WriteLine();
}

Console.WriteLine("Tokenization at the OPT boundary — the transaction record downstream:");
Console.WriteLine();

var vault = new FpeTokenVault();
string token = await vault.TokenizeAsync(pan, CancellationToken.None);

Console.WriteLine($"  cleartext PAN (inside OPT only) : {pan.Reveal()}");
Console.WriteLine($"  masked PAN (logs)               : {pan}");
Console.WriteLine($"  surrogate token (downstream)    : {token}");
Console.WriteLine($"    - same length                 : {token.Length == pan.Reveal().Length}");
Console.WriteLine($"    - passes Luhn                 : {Luhn.IsValid(token)}");
Console.WriteLine($"    - last four preserved         : {token[^4..] == pan.Reveal()[^4..]}");
Console.WriteLine();
Console.WriteLine("  Journal / MQ / dashboard record (no cleartext PAN crosses the OPT boundary):");
Console.WriteLine($"    {{ \"token\": \"{token}\", \"pan\": \"{pan}\", \"amount\": 4500, \"result\": \"APPROVED\" }}");
Console.WriteLine();

return 0;

static byte[] LoadTestBdk()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "testdata", "keys", "bdk.hex")))
    {
        dir = dir.Parent;
    }

    string path = dir is not null
        ? Path.Combine(dir.FullName, "tests", "testdata", "keys", "bdk.hex")
        : throw new FileNotFoundException("Could not locate tests/testdata/keys/bdk.hex from the demo.");

    return Convert.FromHexString(File.ReadAllText(path).Trim());
}
