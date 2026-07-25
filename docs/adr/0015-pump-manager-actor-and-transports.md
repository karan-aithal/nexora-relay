# 0015 — Per-pump actor concurrency, and where the transports live

## Context

Phase 4 requires up to eight pumps concurrently, "one `Channel<T>` per pump for inbound frames,
one for outbound commands", **no locks around shared mutable pump state**, cancellation threaded
everywhere, clean shutdown with no orphaned tasks, sequence-number command/response correlation
with timeout and retry, reconnect with backoff, and a mirrored C# FSM that flags illegal
transitions the firmware reports. The three transports (TCP, serial, in-proc) must be the same
`IPumpTransport` port.

## Decision

- **Each pump is a single-threaded actor (`PumpSession`).** Two unbounded channels — inbound
  frames and outbound commands — are drained by one processing loop that owns *all* mutable state
  (expected FSM state, the pending command, sequence counters). Because only that loop touches the
  state, there are **no locks**; "no cross-talk" falls out of each pump having its own actor and
  channels.
- **Command/response correlation by SEQ** with `IClock`-measured timeout and retransmit (same
  SEQ), `MaxRetries` then a `Timeout` result. Every timeout comes from config and `IClock`, so the
  retry logic is tested deterministically on a self-advancing fake clock — no real waiting.
- **A mirrored dispenser FSM** (`DispenserFsm`, the C# twin of `fsm.c`) is advanced on every acked
  command and observed event; a transition the table forbids is surfaced as a **protocol
  violation** rather than trusted. An exhaustive test asserts the two languages' tables agree.
- **`PumpFrame.Payload` carries the OFP-1 logical body (`SEQ|CMD|PAYLOAD`).** The wire framing
  (STX/LEN/CRC/ETX/stuffing, `OfpWireCodec`) is a transport concern: `TcpPumpTransport` and
  `SerialPumpTransport` wrap/unwrap it; `InProcPumpTransport` carries the body verbatim with no
  framing.
- **Transport placement:** the codec and `TcpPumpTransport` live in `PumpManager` (mirroring how
  the TCP `HostClient` lives in `HostSimulator`); `InProcPumpTransport` lives in `Adapters.InProc`
  and needs no codec (Abstractions-only); `SerialPumpTransport` lives in the Windows-only
  `Adapters.Serial` and references `PumpManager` for the shared codec. **No new adapter project.**

## Consequences

- The concurrency deliverable is met without a single `lock`; a test runs eight pumps under load
  asserting no cross-talk and that `StopAsync` completes with every loop finished.
- The wire codec is written once and shared, so the exact bytes match on socket and serial line;
  the in-proc path stays allocation-light and deterministic for CI.
- `TcpPumpTransport` owning its own reconnect (backoff + jitter) means `ReceiveFrames` keeps
  yielding across a drop; the session resynchronises with a STATUS read on reconnect.

## Alternatives considered

- **Locks around a shared pump-state object.** Rejected: the actor/channel model the phase
  mandates is both simpler to reason about and lock-free by construction.
- **A dedicated `OpenForecourt.Adapters.Tcp` project.** Rejected as an unneeded project; the repo
  already places TCP client code in the consuming project (`HostSimulator`), and the serial
  adapter can reference `PumpManager` for the codec.
- **Mocking `IPumpTransport`'s consumer (a fake `ITransactionService`).** Rejected per CLAUDE.md
  §3: the seam is the transport port, so tests use the real `InProcPumpTransport` with a scripted
  device, not a mock above the port.
- **Pipelined (multiple outstanding) commands.** Rejected for now: single-deep matches the
  firmware's single-deep idempotency window (ADR 0013) and keeps correlation trivial.
