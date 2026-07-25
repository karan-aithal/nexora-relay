# Phase 3 walkthrough — DUKPT, PIN blocks, tokenization, threat model

## 1. Design rationale

Phase 3 is the security substrate: the code that makes the P2PE and PIN-security stories in
CLAUDE.md §7 architecturally true rather than aspirational. One project, `OpenForecourt.Crypto`,
with four independent pieces behind the ports Phase 0 defined:

- **`Dukpt/`** — ANSI X9.24-1 TDES DUKPT: `Ksn` (the key serial number + 21-bit counter) and
  `DukptTdes` (BDK→IPEK, per-transaction key derivation, PIN/data variants, block encrypt).
- **`Pin/`** — ISO 9564 `PinBlock`: Format 0 (PAN-XOR, TDES) and Format 4 (AES).
- **`Tokenization/`** — `FpeTokenVault` (the `ITokenVault` at the OPT boundary) and `Luhn`.
- **`KeyStore/`** — `DpapiKeyStore` (Windows, at-rest) and `InMemoryKeyStore` (tests, blocked in
  Release).

The shape follows one governing fact: **this is the place not to fabricate** (CLAUDE.md §11). So
the verification strategy *is* the design. The BDK→IPEK derivation is anchored on the one DUKPT
vector that is universally published and can be trusted; everything else is proven by round-trip
self-consistency — the terminal derives a key and encrypts, the host independently re-derives
from the BDK and decrypts to the same plaintext — and the genuinely unsourced details (the
data-key variant recipe, the Format 4 layout) carry `SPEC-UNVERIFIED` and are listed here and in
the threat model. That is ADR 0010. The tokenization design (ADR 0011) and the key-store split
(ADR 0012) follow the same "obviously correct and testable over clever" instinct.

The demo (`scripts/demo-03.*`) prints the whole story: derive the IPEK from the test BDK, show
the KSN advancing across three transactions with a different derived key each time, encrypt and
decrypt a PIN block through those keys, and print a downstream record with the token in place of
the PAN.

## 2. The three hardest parts, line by line

### 2.1 The non-reversible DUKPT key derivation

This is the heart of DUKPT and the easiest thing in the project to get wrong. From the IPEK and
the KSN's 21-bit counter, `DeriveBaseKey` produces the per-transaction key:

```csharp
ulong reg = ksn.CounterlessLow64;   // rightmost 8 bytes of the KSN, counter bits cleared
uint counter = ksn.Counter;         // the 21-bit transaction counter

byte[] curKey = ipek.ToArray();
for (uint shift = 0x100000; shift != 0; shift >>= 1)   // walk bits 20..0, high to low
{
    if ((counter & shift) == 0) continue;   // only *set* counter bits generate a key
    reg |= shift;                            // fold this bit into the register
    byte[] next = GenerateKey(curKey, reg);
    CryptographicOperations.ZeroMemory(curKey);
    curKey = next;
}
```

The subtlety: the counter is not "how many times to iterate". It is a **bit pattern**, and each
`1` bit, processed high-to-low, folds into the register and derives the next key from the current
one. Counter 3 (`...011`) does two derivations (bits 1 and 0); counter 4 (`...100`) does one (bit
2). This is why counters with the same number of set bits cost the same and why the counter can
be an opaque 21-bit value rather than a loop bound.

"Non-reversible" is `GenerateKey` → `EncryptRegister`:

```csharp
// bottom = DES_encrypt(keyLeft, reg XOR keyRight) XOR keyRight
for (int i = 0; i < 8; i++) bottom[i] = (byte)(reg[i] ^ keyRight[i]);
byte[] enc = DesEcbEncryptBlock(keyLeft, bottom);
for (int i = 0; i < 8; i++) enc[i] ^= keyRight[i];
```

A single DES under the left half of the key, with the right half XOR'd in on both sides.
`GenerateKey` runs this under the key **and** under the key XOR the variant mask
(`C0C0C0C0...`) to fill the two halves of the 16-byte result. Because each step encrypts *the
register* under *the current key* (not the reverse), you cannot run it backwards: given a later
key you cannot recover an earlier one. That is DUKPT's forward security — capturing one
transaction's key exposes that transaction, not the device's history. The proof it is right: the
IPEK vector matches (`DukptTests.Ipek_matches_the_published_x924_vector`), and this same
machinery derives the IPEK.

### 2.2 Making a token pass Luhn by touching one digit

`FpeTokenVault` keeps the real last four and fills the leading positions from an HMAC, then must
make the whole thing pass Luhn **without disturbing the last four**:

```csharp
private static void FixLuhn(Span<char> token)
{
    for (int candidate = 0; candidate < 10; candidate++)
    {
        token[0] = (char)('0' + candidate);
        if (Luhn.Checksum(token) == 0) return;
    }
}
```

Why is one middle digit always enough? The Luhn checksum is a sum mod 10 of each digit's
contribution. A digit at an *undoubled* position contributes its own value, so cycling it 0..9
cycles the total through all ten residues — exactly one lands on 0. A digit at a *doubled*
position contributes `2d` folded (`2d-9` when `>9`); over `d = 0..9` that mapping is
`{0,2,4,6,8,1,3,5,7,9}` — a permutation of the residues mod 10, so again exactly one candidate
hits 0. Either way the loop is guaranteed to succeed, and it only ever writes `token[0]`, a
middle position, so the preserved last four are untouched. This is the whole trick to a
format-preserving, Luhn-valid, receipt-friendly token that reveals nothing (the other middle
digits are HMAC output).

### 2.3 The Format 0 PIN block, and why the PAN is needed to decrypt it

`EncodeFormat0` builds two 8-byte fields and XORs them:

```csharp
// PIN field: 0 | length | PIN digits | F-fill   → e.g. 1234 becomes 041234FFFFFFFFFF
// PAN field: 0000 | rightmost 12 PAN digits excluding the check digit
for (int i = 0; i < 8; i++) pinField[i] ^= panField[i];   // 041234FF.. XOR 00001111.. = 041225EE..
```

The PAN is mixed into the *cleartext* PIN block before encryption. This is the classic ISO 9564
Format 0 design and it has a real security purpose: the same PIN under the same key produces a
*different* block for a different PAN, so an attacker cannot build a dictionary of "encrypted PIN
1234" across accounts. The consequence lands in `DecryptFormat0`: after TDES-decrypting you must
XOR the **same PAN field** back out before you can read the length nibble and the digits —
decryption needs the PAN, not just the key:

```csharp
byte[] clear = DukptTdes.DecryptBlock(tdesKey, cipher);
for (int i = 0; i < 8; i++) clear[i] ^= panField[i];   // undo the PAN mix
int length = clear[0] & 0x0F;                          // now the length nibble is readable
```

Decrypting against the *wrong* PAN yields a garbage PIN field — which is exactly what
`Format0_wrong_pan_does_not_recover_the_pin` asserts, and why the clear-block value
`041225EEEEEEEEEE` is pinned as a known vector (`Format0_clear_block_matches_the_known_construction`).

## 3. What was rejected and why

- **Fabricating a full per-counter DUKPT vector table** — the exact failure CLAUDE.md §11 names.
  Round-trip self-consistency plus the one trusted IPEK vector proves correctness honestly.
- **BouncyCastle to sidestep .NET's DES weak-key check** — a whole crypto dependency for an edge
  the tested values never hit. See ADR 0010.
- **FF1/FF3-1 format-preserving encryption for tokens** — the production-correct primitive, but
  heavy and easy to get subtly wrong; HMAC-plus-map gives every required property with obviously
  correct code (ADR 0011).
- **A runtime flag to pick the key store** — the Release block must be un-bypassable, so it is a
  compile symbol, not config (ADR 0012).
- **A MAC key variant** — not required by the phase; PIN and data variants only, and the threat
  model states the missing message MAC as a gap.

## 4. Self-quiz

1. In DUKPT, why does the transaction counter behave as a bit pattern rather than an iteration
   count, and what does that imply about the cost of deriving the key for counter 3 versus
   counter 4?
2. Explain precisely why DUKPT is "non-reversible": which operation in `EncryptRegister` makes an
   earlier transaction key unrecoverable from a later one, and why?
3. Why is the PAN XOR'd into the cleartext of an ISO 9564 Format 0 PIN block, and what does that
   force `DecryptFormat0` to require as an input besides the key and the ciphertext?
4. `FpeTokenVault` changes a single middle digit to satisfy Luhn. Prove that one digit is always
   sufficient, covering both the doubled and undoubled Luhn positions.
5. `DpapiKeyStore` and a hardware security module both "protect keys". State the specific attack
   a real HSM stops that `DpapiKeyStore` does not, and where the threat model says so.

## 5. Known weaknesses

- **No tamper-responsive hardware.** The OPT is a process; the IPEK and derived keys sit in
  ordinary memory. This is the biggest gap and the threat model's §6 leads with it.
- **Partial vector verification.** Only BDK→IPEK is checked against an external vector.
  Per-counter advancement and the PIN/data variants are round-trip-proven; the data-variant
  recipe and Format 4 layout are `SPEC-UNVERIFIED`.
- **Simulation-grade token vault.** In-memory map, ephemeral HMAC key. A production vault is an
  HSM-backed FPE service with durable, access-controlled mapping.
- **TDES only.** AES-DUKPT (X9.24-3) is not implemented; Format 4 uses a raw AES key.
- **No MAC, no channel auth, no dashboard authz** — all listed in the threat model.
- **The PAN-scan test covers the artefacts that exist in Phase 3** (token, masked PAN, PIN block,
  KSN). It extends to the SQLite journal and RabbitMQ payloads when those land in Phase 5.
- **.NET DES weak-key check** could throw if a derived key ever collided with a DES weak key; no
  tested value does, and BouncyCastle was deliberately not pulled in for it (ADR 0010).
