# 0009 — The EMV terminal as an explicit, online-only state machine

## Context

The terminal (EMV kernel) runs the transaction flow: candidate selection, GET PROCESSING
OPTIONS, read application data, processing restrictions, terminal risk management, cardholder
verification, terminal action analysis, first GENERATE AC, and field-55 assembly. EMV kernels
are notorious for implicit state spread across flags. CLAUDE.md section 6 requires state machines
to be explicit, and section 5 fixes the scope: **online authorisation (ARQC) only** through
Phase 7 — no offline data authentication (SDA/DDA/CDA).

## Decision

- **One method per EMV step**, called in order from `RunAsync`, each returning a
  `Result<…, TerminalError>` that short-circuits the flow on failure. The steps are named for the
  EMV book stages, so the code reads as the specification's flow chart.
- **A single `TagStore`** accumulates every data element — terminal-resident tags seeded up
  front, card tags absorbed as records are read, GENERATE AC outputs at the end. DOLs (PDOL,
  CDOL1) and field 55 are built by looking tags up in this one store; there is no per-tag
  plumbing.
- **`Tvr`** is a typed 5-byte record with one named setter per bit the terminal sets. The
  Terminal Verification Results are the observable output of the middle steps, and the branch
  tests assert on specific bits (floor limit, expired application, online PIN, random selection).
- **Online-only is enforced structurally.** The kernel performs no offline data authentication,
  so it always sets the TVR "offline data authentication was not performed" bit; combined with
  the terminal/issuer *online* action codes, terminal action analysis always requests an ARQC
  (unless an action code demands denial). The card's returned CID decides the final outcome, so a
  card can still decline offline (return an AAC) — which is exactly the decline profile.
- **The APDU trace is recorded** on the terminal (`Trace`) so a transaction can be printed step by
  step and pinned by a golden test with a fixed unpredictable number and clock.

## Consequences

- Every branch the phase asks for is a unit test over an in-process card: below/above floor
  limit, expired card, and each CVM path (online PIN, signature, no CVM) plus the offline decline.
- The exact command/response byte sequence is a golden test, so a change to any APDU is caught.
- The kernel is a subset. SDA/DDA/CDA, second GENERATE AC, issuer authentication and scripts are
  out of scope and listed in the walkthrough's known weaknesses. Some EMV detail (AUC bit layout,
  exact ATC increment moment) is marked `SPEC-UNVERIFIED`.

## Alternatives considered

- **A table/enum-driven state machine engine.** Rejected as over-engineering for a linear flow;
  a sequence of named methods is the explicit machine CLAUDE.md asks for, and reads better.
- **Attempting offline data authentication now.** Rejected: explicitly out of scope until an
  optional later phase; doing it early would add cryptography this phase does not need.
