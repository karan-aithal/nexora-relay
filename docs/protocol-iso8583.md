# OFC-87 — the OpenForecourt ISO 8583 dialect

**Status:** normative for Phase 1 onward. Code must follow this document; if the two
disagree, the document is wrong and both get fixed.

## Why a "dialect" at all

There is no such thing as "just ISO 8583 on the wire". The standard fixes the *shape*
(MTI, bitmaps, a numbered field set) but leaves every acquirer free to choose:

- which fields are mandatory, optional or unused,
- how lengths are encoded (ASCII digits vs BCD vs binary),
- how numeric fields are represented (ASCII vs packed BCD),
- what the length prefix of a variable field counts (characters vs bytes vs digits),
- proprietary use of the reserved fields.

Real integrations therefore work from the acquirer's *interface specification*, not from
the ISO document. Visa's BASE I, Mastercard's Customer Interface Specification, and every
national scheme differ in all of the above. A codec that assumes one dialect and hard-codes
per-field parsing cannot be pointed at a second acquirer.

**OFC-87** is this project's own documented dialect, based on the **ISO 8583:1987** field
set. It is deliberately simple (ASCII everywhere except the bitmaps and field 55) so that a
hex dump is readable by eye during development. The codec itself is data-driven: swapping
in a packed-BCD dialect is a new field table, not new parsing code.

## Message layout

```
+---------+----------------+------------------+------------------+
| MTI     | primary bitmap | secondary bitmap | field data ...   |
| 4 bytes | 8 bytes        | 8 bytes, if any  | ascending order  |
+---------+----------------+------------------+------------------+
```

- **MTI** — 4 ASCII digits, e.g. `0200`.
- **Primary bitmap** — 8 raw bytes (not hex text), covering fields 1..64.
  Bit 1 is the most significant bit of byte 0. Bit *n* set means field *n* is present.
- **Bit 1 is not a field.** It means "a secondary bitmap follows".
- **Secondary bitmap** — 8 raw bytes covering fields 65..128, present only when bit 1 is
  set. It is emitted only if at least one field above 64 is present.
- **Field data** — the present fields, in ascending field-number order, each encoded per
  the table below.

## Field encodings

| Encoding | Meaning | Encoded as |
|---|---|---|
| `n` | numeric | ASCII digits `0`–`9` |
| `an` / `ans` | alphanumeric (+ special) | printable ASCII, `0x20`–`0x7E` |
| `b` | binary | raw bytes |

| Type | Meaning | Length prefix |
|---|---|---|
| `Fixed` | always exactly *L* units | none |
| `LLVAR` | 0..*L*, *L* ≤ 99 | 2 ASCII digits |
| `LLLVAR` | 0..*L*, *L* ≤ 999 | 3 ASCII digits |

The length prefix counts **units of the field's encoding**: characters for `n`/`an`,
**bytes** for `b`. For OFC-87 one ASCII character is one byte, so the distinction only
matters for field 55.

Padding of fixed-length fields on encode:

- `n` — right-justified, zero-filled on the left (`123` in `n 6` → `000123`)
- `an`/`ans` — left-justified, space-filled on the right (`OPT1` in `ans 8` → `OPT1    `)
- `b` — no padding; the value must already be exactly *L* bytes

## Field table

| # | Name | Type | Length | Enc | Notes |
|---|---|---|---|---|---|
| 2 | Primary account number | LLVAR | 19 | n | **Sensitive** — masked in every trace |
| 3 | Processing code | Fixed | 6 | n | `00` purchase, `01` withdrawal, … + account types |
| 4 | Amount, transaction | Fixed | 12 | n | minor units, no decimal point |
| 7 | Transmission date and time | Fixed | 10 | n | `MMDDhhmmss`, UTC |
| 11 | System trace audit number (STAN) | Fixed | 6 | n | unique per terminal per day |
| 12 | Time, local transaction | Fixed | 6 | n | `hhmmss` |
| 13 | Date, local transaction | Fixed | 4 | n | `MMDD` |
| 22 | Point of service entry mode | Fixed | 3 | n | see below |
| 37 | Retrieval reference number | Fixed | 12 | an | |
| 38 | Authorisation identification response | Fixed | 6 | an | the "auth code" |
| 39 | Response code | Fixed | 2 | an | see below |
| 41 | Card acceptor terminal identification | Fixed | 8 | ans | terminal id |
| 42 | Card acceptor identification code | Fixed | 15 | ans | merchant id |
| 49 | Currency code, transaction | Fixed | 3 | n | ISO 4217 numeric, e.g. `826` = GBP |
| 55 | Integrated circuit card data | LLLVAR | 999 | b | BER-TLV, EMV tags |
| 70 | Network management information code | Fixed | 3 | n | `001` sign-on, `002` sign-off, `301` echo |
| 90 | Original data elements | Fixed | 42 | n | see below |

### `SPEC-UNVERIFIED` notes

These are the points where the exact 1987 text was not available while writing this, so the
project pins a choice explicitly rather than guessing silently:

- **Field 22** — ISO 8583:1987 defines POS entry mode as `n 3` (entry mode 2 digits + PIN
  capability 1 digit); the 1993 revision widens it to `n 12`. OFC-87 uses `n 3`.
  Values used here: `051` chip with PIN capability, `071` contactless chip, `901` magnetic
  stripe. Only `051`/`071` are produced by this project.
- **Field 49** — some publications list the transaction currency code as `a 3` (alphabetic),
  others as `n 3` (numeric). OFC-87 uses `n 3`, ISO 4217 *numeric*.
- **Field 55** — in ISO 8583:1987 field 55 is reserved. Carrying EMV BER-TLV in field 55 is
  an industry convention introduced with chip and formalised in the 1993/2003 revisions and
  in scheme specifications. OFC-87 adopts that convention because it is what a real
  forecourt terminal does.
- **Field 90** — 42 digits laid out as
  `original MTI (4) | original STAN (6) | original transmission date-time (10) |
  acquiring institution id (11) | forwarding institution id (11)`.
  This is the widely published 1987 layout; the trailing institution identifiers are
  zero-filled by this project, which has no institution registry.

### Message type indicators in use

| MTI | Meaning |
|---|---|
| `0100` / `0110` | Authorisation request / response (no funds moved; pre-auth at the pump) |
| `0200` / `0210` | Financial request / response (completion — the actual sale) |
| `0400` / `0410` | Reversal request / response |
| `0800` / `0810` | Network management request / response (sign-on, echo test) |

### Response codes used by the host simulator

| Code | Meaning |
|---|---|
| `00` | Approved |
| `05` | Do not honour |
| `12` | Invalid transaction (unsupported MTI) |
| `13` | Invalid amount |
| `21` | No action taken (reversal of an unknown original) |
| `51` | Insufficient funds |
| `54` | Expired card |
| `94` | Duplicate transmission (same STAN + terminal id) |

## Transport framing

Each message is preceded by a **2-byte big-endian length prefix** giving the number of
bytes in the message body that follows. The prefix does not include itself.

```
+--------+--------+===================+
| len hi | len lo | body (len bytes)  |
+--------+--------+===================+
```

- Maximum accepted body length is configurable; the default is 4096 bytes. A prefix larger
  than the maximum is a protocol error: the reader reports it and the connection is closed
  rather than the reader allocating an attacker-chosen buffer.
- A length of zero is a protocol error.
- TCP gives a byte stream, not messages. A read may return part of a frame, or several
  frames at once. The reader must handle both; there are tests for both.

## What is deliberately not implemented

- No message authentication code (field 64/128). Real acquirer links MAC every message.
- No packed BCD, no EBCDIC. Both are common in the field.
- No field 52 (PIN block) or field 35 (track 2) yet — added in Phase 3, when there are
  keys to protect them with.
- No stand-in processing, no store-and-forward. That is Phase 5.
