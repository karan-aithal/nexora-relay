# 0018 — Offline authorisation policy, and the ports-centric orchestration scope

## Context

Phase 5 requires the site controller to detect host unavailability, authorise offline below a
configured floor limit, queue offline transactions durably, replay them in order on
reconnection, and generate a reversal/exception if a replayed transaction is declined — with a
configurable per-transaction ceiling and a total exposure limit. It also has to decide how much
of the existing estate (real EMV OPT, real pump firmware, real host link) it wires end to end.

## Decision

- **Two limits, enforced by `OfflinePolicy`.** A per-transaction **floor limit** and a total
  **offline exposure ceiling** (sum of offline authorisations not yet replayed). Exposure is
  reserved on grant, released on replay, and rebuilt from the journal on startup so a crash
  never loses track of what the site is carrying.
- **Availability is a probed signal.** `HostProber` pings the host link on an `IClock`-driven
  interval and moves `HostAvailability`; the down→up edge fires `CameOnline`, which wakes
  `OfflineReplayService` to drain the queue oldest-first. A no-response mid-authorisation marks
  the host down *and* reverses that transaction (its state is unknown — ADR 0016), so the next
  customer is served offline rather than double-risked.
- **Replay is exactly-once.** Each offline record is driven terminal by `ReplayOfflineAsync`
  (approved → settle → `Completed`; declined → reversal/exception → `Reversed`), and the host
  dedupes on transaction id, so a crash mid-drain cannot replay a record twice.
- **Ports-centric orchestration scope.** The site controller orchestrates *through the ports*:
  the real host link (TCP ISO 8583 to the host simulator), the SQLite journal, RabbitMQ
  dispatch, and **in-process pump state**. Driving the real pump firmware and the real EMV OPT
  end to end is deliberately left to later phases; the money lifecycle settles at authorisation
  time and the fuel-flow animation is a Phase 6 (frontend) concern.

## Consequences

- The offline story is provable: a test takes 20 offline authorisations, brings the host back,
  and asserts all 20 replay exactly once with exposure returning to zero; the demo shows offline
  authorisation continuing while the host is down and reconciling when it returns.
- `docker compose up` brings up three services — RabbitMQ, host simulator, site controller —
  not the pump firmware, because nothing on this orchestration path connects to it. The firmware
  has its own host build and demo (Phase 4, `scripts/demo-04`).
- Settling at authorisation time is a simplification relative to a real pre-auth-then-capture
  forecourt flow; it is honest about the Phase-5 focus (failure paths) and documented as a known
  weakness in the walkthrough.

## Alternatives considered

- **Full end-to-end wiring (card → OPT → pump firmware → host) in Phase 5.** Rejected as scope
  creep that overlaps Phases 6–7 and buries the failure-path work this phase is graded on.
- **Derive availability only from live traffic** (no prober). Rejected: with no traffic the site
  would never notice the host returning, so replay would never fire.
- **Single global offline limit** instead of floor + exposure ceiling. Rejected: the spec asks
  for both, and they guard different risks (single large sale vs. accumulated exposure).
