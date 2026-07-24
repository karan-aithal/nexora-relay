# 0005 — Separate the host's decision from its transport and its timing

## Context

The acquiring host simulator must approve, decline, inject latency, inject total silence
(to exercise terminal timeouts), detect duplicates and reverse transactions — all over async
TCP, with several connections sharing one ledger.

The interesting behaviour is time-dependent: "this PAN gets 1.5 seconds of latency", "this
PAN never answers". If that behaviour is implemented as `Task.Delay` calls buried inside the
decision logic, it can only be tested by actually waiting, and the concurrency of the ledger
gets tangled up with socket handling.

## Decision

Three layers, each independently testable:

1. **`HostEngine`** — pure decision. `Handle(request)` returns a `HostDecision(Response,
   Latency, Note)`. Latency is a *value*; silence is `Response == null`. The engine never
   sleeps and never touches a socket. It shares only the concurrent `HostLedger`.
2. **`HostServer`** — transport. One task per accepted connection, all sharing one engine.
   It reads frames, calls the engine, applies the returned latency with a real delay, and
   writes the response. It is the only layer that sleeps or sockets.
3. **`HostLedger`** — a lock-free `ConcurrentDictionary` keyed on terminal id + STAN.
   Duplicate detection is `TryAdd`; reversal is a compare-and-swap so two racing reversals
   of one original cannot both succeed.

## Consequences

- Every decision — including the latency and silence rules — is unit-tested in microseconds
  with a `FixedClock`, no sockets and no waiting.
- The one genuine concurrency claim (N terminals racing on a shared STAN, exactly one
  approved) is tested over real loopback TCP against the shared ledger.
- The engine's timing decisions are testable but the *server's* application of them is only
  covered by a couple of wall-clock TCP tests; those use short, generous timeouts. A
  future `IClock`-driven server delay would remove the remaining real waits.

## Alternatives considered

- **One class doing decode + decide + delay + write.** Rejected: the time-dependent
  behaviour would only be testable by waiting, and the ledger concurrency would be
  entangled with socket lifecycle.
- **A lock around the ledger.** Rejected: a `ConcurrentDictionary` with a CAS on the one
  read-modify-write path (reversal) is simpler to reason about than a mutex and makes the
  "exactly one approval under contention" property fall out of `TryAdd`.
- **`IClock` inside `HostServer` for the response latency.** Deferred, not adopted: the
  server's only real waits are the injected latencies, and the TCP tests already tolerate
  them. Worth revisiting if the server grows more timing logic.
