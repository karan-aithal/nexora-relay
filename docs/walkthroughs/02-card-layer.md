# Phase 2 walkthrough — Card layer: BER-TLV, virtual card, PC/SC, EMV online flow

## 1. Design rationale

Phase 2 builds a terminal that runs a real EMV online-authorisation flow against a card we
also wrote, over the same `ICardReader` boundary the physical Windows PC/SC reader implements.
Four projects:

- **`OpenForecourt.Emv`** — the BER-TLV codec (`BerTlv/`) and the terminal kernel (`Terminal/`).
- **`OpenForecourt.VirtualCard`** — the card side: an APDU responder driven by JSON profiles.
- **`OpenForecourt.Adapters.InProc`** — `InProcCardReader`, the reader CI runs against.
- **`OpenForecourt.Adapters.Pcsc`** — `PcscCardReader`, the real `winscard.dll` P/Invoke path.

The shape follows two facts. First, everything the card and terminal exchange is BER-TLV, and
cryptograms are computed over exact byte ranges, so the codec must round-trip **byte for byte**
and must **never throw** on card input (ADR 0006). Second, simulation is one implementation of a
port, not a special case (CLAUDE.md section 3): the terminal talks only to `ICardReader`, so the
in-process virtual card and a physical ACR122U are interchangeable by configuration. The virtual
card is therefore data-driven and encodes real TLV with the production writer (ADR 0007), and the
terminal is an explicit, online-only state machine (ADR 0009).

The flow the demo prints, per card: SELECT PPSE/PSE → build candidate list → SELECT AID → GET
PROCESSING OPTIONS → READ RECORD per AFL → GET DATA (ATC) → processing restrictions → terminal
risk management → CVM selection → terminal action analysis → GENERATE AC (ARQC) → assemble
ISO 8583 field 55.

## 2. The three hardest parts, line by line

### 2.1 Packing a BER tag into an integer without losing its bytes

A BER tag is one or more bytes. When the low 5 bits of the first byte are all set (`0x1F`), the
tag continues; each subsequent byte sets bit 8 to say "more follows". The naïve representation is
a `byte[]`, but then equality and dictionary lookup are awkward and allocate. Instead
[`Tag`](../../src/OpenForecourt.Emv/BerTlv/Tag.cs) packs the bytes big-endian into a `uint`:

```csharp
uint value = input[0];
int i = 1;
if ((input[0] & 0x1F) == 0x1F)      // low 5 bits set => multi-byte tag
{
    do
    {
        if (i >= input.Length || i >= 4) return false;   // truncated, or longer than we support
        value = (value << 8) | input[i];
    }
    while ((input[i++] & 0x80) != 0);                     // bit 8 set => keep going
}
```

The subtlety that makes packing *lossless*: a valid tag's first byte is never `0x00`, so there
are no leading zero bytes to lose. `Bytes` reconstructs the exact on-wire encoding by emitting
`Length` significant bytes big-endian, and `Length` is derived from the numeric magnitude:

```csharp
public int Length => Value switch { <= 0xFF => 1, <= 0xFFFF => 2, <= 0xFFFFFF => 3, _ => 4 };
```

So `9F26` parses to the integer `0x9F26`, compares and hashes as an integer, and serialises back
to exactly `9F 26`. That is what lets `EmvTags` use a plain `Dictionary<uint, string>` and what
underpins the byte-identical round-trip guarantee.

### 2.2 Length parsing that refuses to trust the card

[`BerTlvCodec.TryReadLength`](../../src/OpenForecourt.Emv/BerTlv/BerTlvCodec.cs) is where a hostile
or truncated card is contained. BER definite length has two forms: short (`< 0x80`, the length
itself) and long (`0x8n`, where `n` is how many length bytes follow). The value `0x80` alone is
the *indefinite* form, which is illegal in EMV. Every branch here is a rejection:

```csharp
if (first < 0x80) { length = first; consumed = 1; return true; }   // short form
if (first == 0x80) return false;                                   // indefinite form: rejected
int numBytes = first & 0x7F;
if (numBytes > 4 || numBytes > input.Length - 1) return false;     // too many, or truncated
long value = 0;
for (int i = 0; i < numBytes; i++) value = (value << 8) | input[1 + i];
if (value > int.MaxValue) return false;                            // absurd length: rejected
```

The parse loop then refuses to read a value that runs past the buffer
(`if (length > input.Length - pos) return Fail(...)`), and `ParseSequence` carries a `depth`
counter checked against `MaxDepth = 16`, so a constructed object nested forty deep returns an
error instead of overflowing the stack. None of these paths throw — they return
`Result<…, TlvError>`. The malformed-input tests (`BerTlvTests.Malformed_input_returns_error…`,
`Deeply_nested_input_is_rejected…`) pin each one.

### 2.3 Driving the transaction online with the TVR, and fitting DOL data to length

Two related pieces in the kernel do the real EMV work.

**Terminal action analysis** decides which cryptogram to ask for. The kernel does no offline data
authentication, so it always sets one TVR bit, and that single bit — ANDed against the "online"
action codes — is what forces every transaction online:

```csharp
// EmvTerminal.ProcessingRestrictions()
_tvr.OfflineDataAuthNotPerformed();          // byte 1, bit 8 (0x80) — always, this kernel is online-only

// EmvTerminal.TerminalActionAnalysis()
byte[] tvr = _tvr.ToBytes();
if (AnyBitSet(tvr, Hex(config.TacDenial)) || AnyBitSet(tvr, iacDenial)) return CryptogramType.Aac;
if (AnyBitSet(tvr, Hex(config.TacOnline)) || AnyBitSet(tvr, iacOnline)) return CryptogramType.Arqc;
return CryptogramType.Tc;
```

`AnyBitSet` is a byte-wise AND across the 5 TVR bytes and the 5 action-code bytes. With the online
action codes non-zero in byte 1, the `0x80` bit always matches, so the kernel requests an ARQC —
unless a *denial* bit is set first. The decline profile still gets an ARQC request here; it is the
**card** that returns an AAC (CID `00`), and `AssembleOutcome` reads the returned CID's top two
bits to set the final `TerminalDecision`. That is why "terminal asked online, card declined
offline" is representable, and the branch test asserts exactly that.

**DOL building** is the fiddly bit that trips people. A Data Object List (the PDOL in tag 9F38, the
CDOL1 in tag 8C) is a list of *tag + length* pairs with **no values** — the terminal must supply a
value of exactly the requested length for each. [`BuildDolData`](../../src/OpenForecourt.Emv/Terminal/EmvTerminal.cs)
walks the DOL, looks each tag up in the `TagStore`, and fits it:

```csharp
private static byte[] FitToLength(byte[] value, int length)
{
    if (value.Length == length) return value;
    var result = new byte[length];
    if (value.Length > length)
        value.AsSpan(value.Length - length).CopyTo(result);          // too long: keep rightmost (numerics are right-justified)
    else
        value.CopyTo(result.AsSpan(length - value.Length));          // too short: right-justify into a zero field
    return result;
}
```

Get this wrong — pad on the wrong side, or forget that the DOL carries no values — and the GPO or
GENERATE AC command is malformed and the cryptogram would be computed over the wrong bytes. The
golden APDU trace test pins the exact command bytes, so this stays correct.

## 3. What was rejected and why

- **A third-party BER-TLV / PC/SC library.** The byte-identity, total-parsing and "real
  production API" guarantees are the point of the phase; a dependency hides exactly what a
  payments reviewer wants to read. (ADR 0006, 0008.)
- **Pre-baked response hex in the card profiles.** Brittle and hides TLV construction; the card
  encodes from structured data instead. (ADR 0007.)
- **`VirtualPcdCardReader` (the third reader adapter).** Deferred to `docs/backlog.md`: it needs a
  running pcscd/vpcd daemon, can't run in CI, and would need wire-protocol bytes reproduced from
  memory. Two real adapters (in-process + winscard) already prove the port thesis. (ADR 0008.)
- **A table-driven state-machine engine for the kernel.** Over-engineering for a linear flow; a
  sequence of named, `Result`-returning methods is the explicit machine CLAUDE.md asks for and
  reads like the EMV flow chart. (ADR 0009.)

## 4. Self-quiz

1. Why can a BER tag be packed into a `uint` without losing information, and where would that
   break if EMV used five-byte tags?
2. Walk through how `TryReadLength` distinguishes the short form, the long form, and the
   indefinite form, and name every input it rejects.
3. The terminal always requests an ARQC yet the decline card ends `DeclinedOffline`. Trace the
   exact bytes and decisions that make both true in the same transaction.
4. A DOL contains `9F02 06`. The `TagStore` holds `9F02 = 00 00 25 00`. What four bytes go on the
   wire, and why is the padding on the side it is?
5. What is in ISO 8583 field 55 after a transaction, why is tag 82 (AIP) in there when the
   terminal never sent it to the card, and which tags come from the card's GENERATE AC response?

## 5. Known weaknesses (simplifications relative to production)

- **Scope is the online (ARQC) subset only** (CLAUDE.md section 5). No offline data authentication
  (SDA/DDA/CDA), no second GENERATE AC, no issuer authentication, no issuer scripts. The kernel
  structurally reflects this by always setting the "ODA not performed" TVR bit.
- **The card's cryptogram (9F26) is a canned placeholder.** This phase exercises the message flow,
  not cryptography. Real card-key ARQC computation and PIN blocks arrive in the crypto phase.
- **Non-minimal BER length encodings are not preserved** on re-serialisation. EMV always uses
  minimal lengths, so the byte-identical guarantee holds for all real inputs.
- **`SPEC-UNVERIFIED` items:** the AUC (tag 9F07) bit checked for "valid at non-ATM terminals",
  and the exact moment the ATC increments (taken as GPO). Both are marked in code.
- **`PcscCardReader` is reviewed by reading, not CI.** It targets `net10.0-windows` and is excluded
  from the Linux CI build; its winscard interop is exercised only on a Windows machine with a
  reader.
- **The raw APDU hex in the demo shows the cleartext test PAN.** That is the literal card↔terminal
  wire, which lives inside the OPT / P2PE boundary; the decoded view and every outcome mask the PAN
  (first 6 + last 4), enforced by `TraceMaskingTests`.
