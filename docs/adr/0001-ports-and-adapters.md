# ADR-0001 — Ports and adapters as the core architecture

## Status
Accepted (Phase 0).

## Context
OpenForecourt must run in three very different modes from one codebase: fully simulated
(CI, no hardware), partially simulated (developer laptop with a virtual card reader or
com0com serial pair), and — in principle — against real hardware (an ACR122U PC/SC
reader, a physical pump controller). Every external boundary therefore has more than one
real implementation.

If simulation were a branch inside the business logic (`if (simulated) …`), that logic
would be untestable in isolation, the simulated and real paths would drift, and "swap the
simulator for hardware" would be a code change touching core flows.

## Decision
Every external boundary is expressed as an interface ("port") in
`OpenForecourt.Abstractions`, which has **zero dependencies**. Concrete implementations
("adapters") live in separate projects and are selected by configuration. Simulation is
one adapter among several, never a special case in business logic.

Ports defined in Phase 0: `ICardReader`, `IPumpTransport`, `IHostConnection`,
`ISecureKeyStore`, `ITransactionJournal`, `IClock`, `ITokenVault`.

The rule "**mock at the port, never above it**" (CLAUDE.md section 3) is enforced by an
architecture test: `OpenForecourt.Abstractions` must reference only the base class
library. If a test ever needs to fake something above a port (e.g. a service), that is a
signal the seam is in the wrong place.

## Consequences
- **Positive:** Swapping a simulated reader for a physical ACR122U is a configuration
  change. The full test suite runs on Linux CI with no hardware. Business logic is
  testable against in-process adapters and a `FakeClock`.
- **Positive:** Failure semantics are documented on the interface, so every adapter must
  honour the same contract (e.g. timeout vs late response on `IHostConnection`).
- **Negative:** More projects and more indirection than a monolith. Justified here because
  the multi-implementation requirement is real, not speculative.

## Alternatives considered
- **Conditional compilation / runtime flags for simulation.** Rejected: couples business
  logic to simulation, defeats isolated testing, guarantees drift.
- **A single "hardware abstraction" god-interface.** Rejected: violates ISP; a card
  reader and a host connection have nothing in common.
