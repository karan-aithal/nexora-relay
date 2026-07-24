# 0008 — Direct winscard.dll P/Invoke, and no third card-reader adapter yet

## Context

The card-reader boundary (`ICardReader`) has, by design, several implementations selected by
configuration (CLAUDE.md section 3). The phase-2 spec lists three:

- `PcscCardReader` — the real Windows PC/SC path.
- `VirtualPcdCardReader` — a `vsmartcard` vpcd reader over TCP.
- `InProcCardReader` — direct calls into the virtual card, for CI on Linux.

## Decision

**`PcscCardReader` uses direct P/Invoke into `winscard.dll` — no wrapper library.** It declares
`SCardEstablishContext`, `SCardListReaders`, `SCardConnect`, `SCardTransmit`,
`SCardGetStatusChange`, `SCardDisconnect`, `SCardReleaseContext` itself (the ANSI variants), and
marshals `SCARD_READERSTATE` / `SCARD_IO_REQUEST` by hand. The blocking calls run on the thread
pool via `Task.Run`, and cancellation is honoured by polling `SCardGetStatusChange` with a short
per-iteration timeout — so the async port contract holds with no `async void`, `.Result` or
`.Wait()` (CLAUDE.md section 6). The project targets `net10.0-windows` and is excluded from the
Linux CI solution filter.

**`InProcCardReader` is built** (Adapters.InProc); it is what CI runs the whole EMV flow against.

**`VirtualPcdCardReader` is deferred to the backlog**, not built. Rationale:

- It requires a running `pcscd` + `vpcd` daemon, so it cannot run in CI and proves nothing the
  other two adapters do not already prove architecturally.
- Its wire protocol would have to be reproduced from memory, which CLAUDE.md section 11 forbids
  without explicit `SPEC-UNVERIFIED` marking — a poor trade for a third, untestable adapter.
- The port-and-adapter thesis ("swap simulated for physical is configuration, not code") is
  already demonstrated by two *real* adapters: an in-process one and a genuine winscard one.

## Consequences

- The headline claim — the terminal cannot tell a simulated card from an ACR122U — is shown by
  code, on the real production API, not a library that abstracts it away.
- `PcscCardReader` cannot be built or tested on the Linux CI box; it builds on Windows dev
  machines and is compiled as part of the full solution there. Its interop is reviewed by
  reading, not by CI.
- Exercising the PC/SC *stack* with no hardware (the value `VirtualPcdCardReader` would add) is
  recorded in `docs/backlog.md` and can be picked up when a demo needs it.

## Alternatives considered

- **PCSC-lite / a managed PC/SC wrapper (e.g. PCSC-Sharp).** Rejected: the point of the adapter
  is that it *is* the real production code path; a wrapper defeats it.
- **Building `VirtualPcdCardReader` now.** Rejected for the reasons above; deferred, not dropped.
