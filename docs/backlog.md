# Backlog

Ideas noted but deliberately out of the current phase's scope (CLAUDE.md section 11: "Never
expand scope beyond the current phase file. Note the idea here and move on.").

## Card layer (Phase 2)

- **`VirtualPcdCardReader` — third `ICardReader` adapter over `vsmartcard` vpcd (TCP).**
  Would exercise the real PC/SC *stack* with no hardware by talking to a `vpcd` virtual reader
  in `pcscd`. Deferred (ADR 0008): needs a running pcscd/vpcd daemon so it cannot run in CI, its
  wire protocol would have to be reproduced and marked `SPEC-UNVERIFIED`, and the port thesis is
  already demonstrated by two real adapters (`InProcCardReader` + `PcscCardReader`). Pick up when
  a demo genuinely needs the PC/SC stack in the loop on Linux.
- **Real ARQC / cryptogram computation in `VirtualCard`.** Currently the GENERATE AC cryptogram
  (9F26) is a canned test value. Belongs with the crypto phase (card keys, session keys).
- **Offline data authentication (SDA/DDA/CDA) in the terminal kernel.** Explicitly optional
  Phase 8; the kernel is online-only until then.
