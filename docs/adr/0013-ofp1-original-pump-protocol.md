# 0013 — OFP-1: an original, documented pump protocol

## Context

Phase 4 needs a wire protocol between the pump controller firmware (C) and the pump manager
(C#). Real forecourt links (IFSF and vendor dialects) are proprietary and partly under NDA;
CLAUDE.md §11 forbids inventing protocol details from memory and reproducing a proprietary spec.
The protocol also has to be teachable line by line and testable across two languages.

## Decision

- **Define an original protocol, OFP-1, in `docs/protocol-pump.md` before writing any pump code.**
  The document is normative; code follows it. The doc-before-code ordering is visible in git
  history (the protocol was its own commit).
- **Framing:** `STX | LEN(2, big-endian) | SEQ | CMD | PAYLOAD | CRC16 | ETX`, with `LEN` counting
  `SEQ+CMD+PAYLOAD` and the CRC covering that same span. Big-endian throughout.
- **CRC-16/CCITT-FALSE**, pinned by its published check value `crc("123456789") == 0x29B1`. The
  name "CRC16-CCITT" is ambiguous (it names ≥3 algorithms); the check value removes the ambiguity
  and both the C and C# implementations assert it.
- **DLE byte-stuffing** (`0x10`, XOR `0x20`) of any `STX/ETX/ESC` inside the frame body, so the raw
  delimiters are unambiguous. Stuffing is applied after LEN/CRC are computed and removed before
  they are checked.
- **Single-deep sequence numbers with idempotent duplicate handling.** One command outstanding at
  a time; a retransmission reuses the SEQ; the firmware caches the last command's response and
  re-sends it for a duplicate SEQ instead of re-executing — essential so a lost AUTHORISE ACK
  never double-authorises.
- **The critical event (DISPENSE_COMPLETE) is delivered at-least-once without a new frame type**:
  the firmware stays in COMPLETE and re-emits it (same event SEQ) until the manager settles.

## Consequences

- The protocol is fully specified and self-consistent, with a CRC-verified worked byte trace in
  the doc; the golden frame in that trace is asserted byte-for-byte by both languages' tests.
- No proprietary material is reproduced; the doc states plainly it is inspired by IFSF, not an
  implementation of it. Any genuinely uncertain detail would be marked `SPEC-UNVERIFIED` — none
  was needed because every value is a project choice.
- The single-deep window keeps duplicate detection to "last SEQ only", which matches
  at-most-one-outstanding-command and avoids a replay window; the cost is no command pipelining.

## Alternatives considered

- **Reproduce an IFSF/vendor dialect.** Rejected: proprietary, partly NDA'd, and would force
  `SPEC-UNVERIFIED` guesses — a confidently wrong parser is worse than an honest original (§11).
- **A different CRC (XMODEM / KERMIT) or a checksum.** Rejected: CCITT-FALSE is a common device
  choice and is pinned unambiguously by its check value; a plain checksum misses bit errors a
  serial line actually produces.
- **COBS instead of DLE stuffing.** Rejected: DLE stuffing is the more familiar, self-evident
  choice for a teachable protocol, and worst-case expansion is bounded and acceptable here.
- **ACKing every event.** Rejected: FLOW_UPDATE loss is self-correcting (superseded by the next),
  and the one critical event is made reliable by re-emission through the existing FSM, so no
  event-ACK frame type is needed.
