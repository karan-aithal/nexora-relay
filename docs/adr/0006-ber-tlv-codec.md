# 0006 — A byte-faithful, fuzz-resistant BER-TLV codec

## Context

EMV data — FCI templates, GPO responses, records, GENERATE AC responses, ISO 8583 field 55
— is all BER-TLV. The card layer must parse TLV that arrives from a card (untrusted, possibly
truncated or malformed) and must also emit TLV that goes back on the wire. Two properties are
non-negotiable:

1. **Byte-identical round trip.** A parsed object re-serialised must reproduce the original
   bytes. Cryptograms are computed over exact byte ranges; a codec that "normalises" on the
   way through would silently break authentication downstream.
2. **Total parsing.** Input off a card must never throw or loop. A truncated tag, a length
   that runs past the buffer, the indefinite-length form, or pathological nesting are all
   *expected* inputs that must return an error value (CLAUDE.md section 6).

## Decision

- **Tags are packed into a `uint`** (`Tag`), big-endian, up to four bytes. A valid tag's first
  byte is non-zero, so packing is lossless and `Bytes` reproduces the on-wire encoding exactly.
  Value-equality and dictionary lookup become trivial integer operations.
- **`TlvNode`** is an immutable tree: a primitive carries a value, a constructed node carries
  children. `WriteTo` emits the minimal BER definite length. EMV always uses minimal lengths,
  so the output is byte-identical for every real input; the one case where it would differ —
  non-minimal input — does not occur in EMV and is documented, not silently handled.
- **`BerTlvCodec.TryParse`** returns `Result<IReadOnlyList<TlvNode>, TlvError>`. It rejects the
  indefinite length form, length-of-length above four bytes, any length that exceeds the
  remaining buffer, and (via a `MaxDepth` guard) adversarially deep nesting. It never throws.
- **`EmvTags`** names the common tags so a trace reads as names, and masks PAN-bearing tags
  (5A, 57) in the decoded view (CLAUDE.md section 7).

## Consequences

- The golden round-trip test asserts byte-identity on real EMV byte strings, so a regression in
  the tag or length encoding fails loudly.
- Malformed-input tests (truncated tag, over-long length, indefinite form, 40-deep nesting) all
  assert an error rather than an exception, pinning the fuzz-resistance contract.
- Non-minimal length encodings are not preserved. This is acceptable for EMV and stated in the
  walkthrough's "known weaknesses".

## Alternatives considered

- **A third-party BER-TLV library.** Rejected: the byte-identity and total-parsing guarantees
  are the whole point of the exercise and are cheap to own; a dependency would hide exactly the
  behaviour a payments reviewer wants to read.
- **Storing tags as `byte[]`.** Rejected: worse equality/lookup ergonomics and more allocation
  for no gain, since four bytes cover every EMV tag.
