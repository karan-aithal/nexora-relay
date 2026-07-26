# 0020 — The outdoor payment terminal runs inside the site controller

## Context

Through Phase 5 the card layer lived in `OpenForecourt.Opt`, a console application that ran an EMV
flow against the virtual card and printed the APDU trace. The site controller knew nothing about
cards: `POST /api/v1/pumps/{id}/authorise` took an opaque token and an amount.

Phase 6 asks for a card simulator, a PIN pad with a visible timeout, and a transaction detail
drawer showing the **full trace** — APDU exchange, ISO 8583 request and response field by field,
and a timing waterfall. None of that is possible while the component that talks to the card is a
separate process that the orchestration tier never invokes. It also leaves the P2PE claim
(CLAUDE.md section 7.4, "cleartext PAN never leaves the OPT boundary") as an assertion about two
components that never actually meet.

## Decision

**Host the OPT inside the site controller as `Opt/OptService`**, and keep `OpenForecourt.Opt` as
the Phase 2 demonstration entry point.

- `PresentCardAsync` builds an `EmvTerminal` over `InProcCardReader(new VirtualCard(profile))` and
  runs the real kernel against the same JSON card profiles the Phase 2 unit tests use.
- The cleartext PAN exists only as a local in that method. It goes straight into
  `ITokenVault.TokenizeAsync`, and what is handed to `TransactionService` is a token. The
  boundary is now a method scope, which is something a reader can check.
- Online PIN is driven by the kernel's CVM selection, not by configuration: if the card's CVM list
  asks for online PIN the terminal parks in `PinRequired` with a deadline, and only continues once
  a PIN has been captured and encrypted under a DUKPT-derived key.
- The terminal follows the pump back to idle (`OptService.Track`) rather than being told by the
  pump fleet, so the dependency stays one-way and the screen clears however the sale ended.

The site controller therefore now references `OpenForecourt.Emv`, `OpenForecourt.Crypto` and
`OpenForecourt.VirtualCard`.

## Consequences

- The trace in the dashboard is captured by the components that did the work. It is not a
  reconstruction, and it cannot drift from what actually happened.
- The P2PE story is architecturally true and testable: `OptFlowTests` asserts that the journal
  holds a token, and that no decoded trace field contains the test PAN.
- The PIN block is built, used and cleared inside `TryEncryptPin`. It is never returned, stored,
  logged or traced; only the KSN reaches the trace, because that is what shows a per-transaction
  key was derived.
- A BDK is required for online-PIN cards. It is deliberately **not** a configuration default in
  `src/` (CLAUDE.md section 7.2): `SiteOptions.PinBdkPath` is null unless the demo or the tests
  point it at `tests/testdata/keys`. With no BDK the terminal refuses the card rather than
  handling a PIN it cannot protect.
- The site controller is now a bigger process. That is a real cost, and it is the honest shape for
  this system: an OPT is a component of a forecourt, not a separate deployment.

## Alternatives considered

- **Leave the OPT as a separate process and give it an HTTP client to the site controller.** More
  faithful to a physical estate, where the terminal really is separate hardware. Rejected for this
  phase because it adds a deployment and a protocol without changing anything the reviewer can
  see, and because the trace would then have to be shipped across that boundary — which is more
  machinery in service of a diagnostic view.
- **Synthesise the trace in the dashboard from REST responses.** Far less work and completely
  dishonest: the drawer would show a plausible reconstruction rather than the bytes that were
  exchanged. The point of the screen is that it is real.
- **Feed the PIN into the EMV kernel before GENERATE AC.** Correct for a real terminal, and the
  kernel does not currently support it. Noted as a known weakness in the Phase 6 walkthrough
  rather than guessed at, per CLAUDE.md section 11.
