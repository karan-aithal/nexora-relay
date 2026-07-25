# 0010 — DUKPT (TDES) implementation and how it is verified

## Context

Phase 3 requires ANSI X9.24-1 TDES DUKPT: BDK→IPEK derivation, KSN structure with a 21-bit
counter, non-reversible future-key derivation, per-transaction PIN and data key variants, and
correct behaviour at counter exhaustion. CLAUDE.md §11 is emphatic: **never invent DUKPT steps
from memory; if the exact vectors cannot be sourced, say so and mark `SPEC-UNVERIFIED`.** This
is the single most important place in the project not to fabricate.

Two facts shape the decision. First, the BDK→IPEK derivation for the canonical worked example
(BDK `0123456789ABCDEFFEDCBA9876543210`, KSN `FFFF9876543210E00000` →
IPEK `6AC292FAA1315B4D858AB3A3D7D5933A`) is the most widely reproduced DUKPT test vector in
existence and can be asserted with confidence. Second, full per-counter derived-key tables and
the exact data-key variant recipe could not be sourced to certainty here.

## Decision

- **Implement the derivation, anchor it on the one vector we trust.** `DukptTdes.DeriveIpek` is
  asserted against the published IPEK. Because IPEK derivation exercises the same TDES/variant
  machinery the rest of the scheme uses, a correct IPEK is strong evidence the primitives are
  right.
- **Prove everything else by round-trip self-consistency.** The terminal derives a key from the
  IPEK and encrypts; the host independently re-derives from the **BDK** (BDK→IPEK→transaction
  key) and decrypts to the same plaintext. This proves the two derivation paths agree and the
  cipher pairing is correct — the property that actually matters operationally — without
  asserting fabricated per-counter vectors.
- **Mark the unsourced parts.** The data-encryption variant mask + self-encryption step and the
  Format 4 PIN-block layout carry `SPEC-UNVERIFIED` comments and are called out in the
  walkthrough and threat model. The PIN variant (`00000000000000FF00000000000000FF`) is used for
  the primary path.
- **Use .NET `TripleDES`/`DES` for the primitives**, with the weak-crypto analyzer warnings
  (CA5350/CA5351) suppressed at each primitive with the justification that TDES *is* the
  algorithm X9.24-1 is defined over and only test keys are involved. Single DES in the register
  step goes through the `DES` class; the 16-byte two-key working keys are expanded to `K1|K2|K1`
  for .NET's 24-byte TripleDES.
- **`Ksn` is a `readonly record struct`** (CLAUDE.md §6) that exposes the 21-bit counter,
  advances it, and returns `CounterExhausted` at `MaxCounter` rather than wrapping — a device is
  retired, not reused.

## Consequences

- The IPEK test is a real external-vector check; the advancement, variant, and round-trip tests
  prove internal correctness. If a future maintainer sources authoritative per-counter vectors,
  they slot in as additional asserts without changing the implementation.
- The honest `SPEC-UNVERIFIED` gaps are documented, not hidden — exactly the posture §11 asks
  for. A reviewer can see precisely what is vector-proven and what is round-trip-proven.
- Suppressing CA5350/CA5351 is unavoidable: the domain mandates TDES. Suppressions are local to
  the primitives with an explicit justification, not a blanket project-wide `NoWarn`.

## Alternatives considered

- **Fabricate a full per-counter vector table from memory.** Rejected outright — this is the
  precise failure §11 names ("a confidently wrong bitmap parser is worse than an honest gap").
- **Add BouncyCastle to avoid .NET's DES weak-key checks.** Rejected: a new dependency for a
  problem the test values do not hit (the canonical vector chain contains no DES weak keys). If a
  derived key ever collided with one of the ~64 weak/semi-weak keys the .NET setter would throw;
  that has not occurred for any tested value, and the trade — a whole crypto dependency vs a
  negligible-probability edge — does not pay. Revisit only if it actually bites.
- **AES-DUKPT (X9.24-3) now.** Rejected: it is the optional stretch; TDES is the locked scope.
