# Phase 0 — Scaffold and Ports

**Goal:** A buildable, testable, CI-green skeleton with every architectural seam
defined and zero business logic.

**Read first:** `CLAUDE.md` sections 3, 4, 5, 6.

## In scope
- Solution and project layout exactly as `CLAUDE.md` section 4
- `Directory.Build.props`, `.editorconfig`, `.gitignore`
- All port interfaces in `OpenForecourt.Abstractions` — signatures only
- Domain model value types
- GitHub Actions CI
- ADR-0001 (ports and adapters), ADR-0002 (.NET 10 LTS), ADR-0003 (SQLite journal)

## Out of scope
Any implementation. No ISO 8583, no EMV, no firmware. This phase produces
interfaces, types and infrastructure only.

## Deliverables

### Port interfaces (`OpenForecourt.Abstractions/Ports/`)
Define, with XML doc comments explaining the contract including failure semantics:

- `ICardReader` — `WaitForCardAsync`, `TransmitAsync(ReadOnlyMemory<byte> apdu)`,
  `Disconnect()`, plus a `CardPresenceChanged` event. Model reader errors as a result
  type, not exceptions.
- `IPumpTransport` — `SendFrameAsync`, `IAsyncEnumerable<PumpFrame> ReceiveFrames`,
  connection state, reconnect semantics documented
- `IHostConnection` — `Task<HostResponse> SendAsync(FinancialRequest, CancellationToken)`,
  with explicit documented behaviour for timeout vs late response
- `ISecureKeyStore`, `ITransactionJournal`, `IClock`, `ITokenVault`

### Domain model (`OpenForecourt.Abstractions/Domain/`)
- `readonly record struct Money(long Minor, string CurrencyCode)` — integer minor units
  only, never `decimal`, never `double`
- `readonly record struct Volume(long MilliLitres)`
- `Pan` — wraps the value, `ToString()` returns the **masked** form, exposes raw only
  through an explicit `Reveal()` method so every unmasking is greppable
- `PumpState` enum: `Idle, Authorising, Authorised, NozzleLifted, Dispensing,
  DispenseComplete, Settling, Error, OutOfService`
- `TransactionContext` — id, pump, state, timestamps, correlation id

### Infrastructure
- `Directory.Build.props` with nullable enabled, warnings as errors, analysis level
- CI workflow: restore, build, test on `ubuntu-latest`; Windows-only adapter projects
  excluded from the Linux job via a solution filter or condition
- `scripts/demo-00.ps1` and `.sh` — prints the solution structure and confirms build +
  test success

## Tests
- One architecture test (NetArchTest or a reflection test) asserting that
  `OpenForecourt.Abstractions` has **no project references** and no dependency on
  ASP.NET, SQLite or RabbitMQ.
- One test asserting `Pan.ToString()` never returns more than 6 leading and 4 trailing
  digits.

## Definition of Done
Per `CLAUDE.md` section 9. The demo script must build and test cleanly from a fresh
clone.
