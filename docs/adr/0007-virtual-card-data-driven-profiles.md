# 0007 — A data-driven virtual card with JSON profiles

## Context

Phase 2 needs the *card side* of the EMV conversation: something that answers SELECT, GET
PROCESSING OPTIONS, READ RECORD, GET DATA and GENERATE AC with correct BER-TLV and correct
ISO 7816-4 status words. It must support at least three behaviours — a contact chip card, a
contactless card, and a card that forces a decline — and it must be a first-class simulator,
not a test mock (CLAUDE.md sections 1 and 3).

Two ways to shape it:

1. Bake each card's responses as hex blobs and have the card replay them.
2. Hold each card as **structured data** (AIDs, per-record tag→value maps, a canned GENERATE
   AC answer) and have the card **encode** the TLV itself.

## Decision

Choose (2). `CardProfile` is pure data loaded from JSON under `tests/testdata/cards/`; the tag
values are hex, but the *TLV structure* — FCI templates (6F/A5/BF0C/61), the GPO format-2
template (77/82/94), record templates (70), the DOL for the PDOL (9F38) — is built by
`VirtualCard` at runtime using the same `TlvNode` writer the terminal parses with.

The card holds the small amount of state a real card holds across a transaction: the selected
application, whether GPO has run (so GENERATE AC before GPO is `6985`), and the Application
Transaction Counter, which increments at GPO and is returned by GET DATA (9F36) and GENERATE AC.

## Consequences

- The test data cannot mis-encode a TLV length — lengths are computed, never hand-counted — so
  the profiles stay correct as they evolve.
- Adding a card is adding a JSON file, not writing code. The three required behaviours are three
  files; a decline is `cid: "00"` (AAC) in the profile.
- Because the card encodes with the production writer and answers real status words, an
  in-process transaction exercises the genuine message flow; the difference between this and a
  physical card is the `ICardReader` implementation, nothing above it.
- The canned cryptogram (9F26) is a placeholder — this phase exercises the message flow, not
  cryptography. Real card-key computation is out of scope until the crypto phase.

## Alternatives considered

- **Replay of pre-baked response hex.** Rejected: brittle (every length hand-encoded), and it
  hides the TLV construction that is half the learning value of the phase.
- **A full Java Card / GlobalPlatform emulation.** Rejected as vast over-scope; the terminal
  only needs correct APDU responses for the online-authorisation subset.
