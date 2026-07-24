# 0004 — A data-driven ISO 8583 codec over a documented dialect

## Context

Phase 1 needs an ISO 8583 codec. Two things about "ISO 8583" make a naïve implementation a
trap:

1. **There is no single wire format.** The standard fixes the message *shape* (MTI, bitmaps,
   a numbered field set) but leaves each acquirer to choose length encodings, numeric
   representations (ASCII vs packed BCD), what a variable field's length prefix counts, and
   the use of reserved fields. Real integrations work from the acquirer's interface
   specification, not from the ISO document. Visa BASE I, the Mastercard CIS and national
   schemes differ in all of these.
2. **Per-field parsing code does not scale and does not port.** A `switch` with a case per
   field number is where bugs hide and where a second acquirer's dialect cannot be added
   without rewriting the parser.

CLAUDE.md section 11 also forbids inventing spec details from memory; where the exact 1987
text is uncertain the choice must be pinned explicitly, not guessed silently.

## Decision

- Define and document the project's **own** dialect, **OFC-87**, in
  `docs/protocol-iso8583.md`, based on the ISO 8583:1987 field set, ASCII everywhere except
  the bitmaps and field 55. Every point where the 1987 text was not verified is marked
  `SPEC-UNVERIFIED` in both the document and the code.
- Make the codec **data-driven**: a `FieldDefinition` table (`Iso8583Dialect`) describes each
  field's type (Fixed/LLVAR/LLLVAR), length and encoding (numeric/alphanumeric/binary). One
  `FieldCodec` encodes and decodes any field from its row. There is no per-field logic
  anywhere. A different acquirer is a different table, not different code.
- Decoding returns `Result<Iso8583Message, CodecError>` and never throws on malformed input
  (CLAUDE.md section 6): every byte comes off a socket, so a bad message is an expected
  outcome carrying an offset and a diagnostic, not an exception.
- `Iso8583Message` is immutable after `Build()`; sensitive fields (the PAN) render masked in
  the trace via the existing `Pan` type, so the diagnostic you live in cannot leak a PAN.

## Consequences

- Adding a field is adding a row. Supporting a second dialect is supplying a second table —
  demonstrated by construction, even though only OFC-87 exists today.
- The golden-file tests can be produced from the *document* by an independent throwaway
  encoder, so the codec cannot define its own truth.
- The dialect is deliberately simpler than a production acquirer link: ASCII rather than
  packed BCD, no MAC field, no PIN block yet. Those are noted as not-implemented in the
  protocol document and arrive in later phases.

## Alternatives considered

- **An existing ISO 8583 library (e.g. jPOS-style).** Rejected: the entire point of the
  phase is to demonstrate the author can write and explain a bitmap parser. A dependency
  would hide exactly the skill being shown, and CLAUDE.md section 8 requires every line be
  explainable.
- **Hard-coded per-field parsing.** Rejected: does not port to a second dialect and
  concentrates bugs, which is the opposite of the deliverable's stated goal.
- **Throwing on malformed input.** Rejected: violates CLAUDE.md section 6 and would turn a
  hostile peer's bad frame into an exception-handling problem instead of a diagnostic.
