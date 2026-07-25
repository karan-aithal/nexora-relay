# 0016 — Journal write-intent-before-send, and the five-point crash recovery model

## Context

Phase 5 is "the part that proves you understand that payments engineering is about failure
paths". The site controller must survive being killed at any point in a transaction and, on
restart, leave every interrupted transaction in a correct terminal or resumable state — never
double-charging, never silently losing an authorised sale, never treating an unknown host
state as approved. The journal is SQLite in WAL mode (ADR 0003); this ADR is about *what* is
written *when*, and how restart reasons about it.

## Decision

- **A `TransactionStatus` lifecycle distinct from `PumpState`.** `Intent → HostRequestSent →
  Approved → Completed`, with `Declined`, `Reversed` and `Voided` as terminal branches.
  `PumpState` is what the dashboard shows; `TransactionStatus` is what recovery reasons about.
- **Every step is journalled before its externally-observable side effect.** The invariant:
  the intent is durable *before* the host request goes on the wire; the approval is durable
  *before* settlement is dispatched; completion is durable *after* the settlement is on the
  queue. Writes are idempotent upserts keyed by transaction id, so replaying a step is
  harmless and the table always holds the latest state.
- **Five kill points, five recovery rules.** On startup `RecoveryService` reads every
  non-terminal record and `ResumeAsync` resolves it:
  - `Intent` — nothing was ever sent to the host → **void**.
  - `HostRequestSent` — the host may or may not have authorised; its state is **unknown** →
    **reverse** (send a reversal advice).
  - `Approved` online, never settled → **resume settlement**.
  - `Approved` offline, awaiting replay → **re-track exposure, leave queued** for the replayer.
  - `Completed` / other terminal → **no-op**.
- **WAL `synchronous=NORMAL`.** Durable across process crash — which is exactly the kill-point
  test — at far lower cost than `FULL`. `FULL` is the upgrade path if true power-loss
  durability against an un-checkpointed WAL is ever required.

## Consequences

- The five kill points are covered by a test that seeds the journal in each status, "restarts"
  by reopening the same SQLite file, runs recovery and asserts the outcome; a companion test
  proves a normal approval really does journal `Intent → HostRequestSent → Approved →
  Completed` in that order, so the kill points are real writes and not fiction.
- The live demo exercises it for real: the site controller is killed while carrying three
  offline authorisations and, on restart, logs "3 in-flight to reconcile" and re-queues them
  with exposure preserved — visible proof the money survived the crash.
- Recovery + at-least-once dispatch + a consumer that dedupes on transaction id together give
  an **exactly-once** end-to-end effect from at-least-once parts.

## Alternatives considered

- **Write after the host responds.** Rejected: a crash between sending and responding leaves no
  record of a possibly-authorised sale — the one case that must never be lost.
- **Append-only event log with replay.** More faithful to real switches, but the latest-state
  upsert is enough to reconcile and is far simpler to query for the REST/SignalR views. Noted
  as a possible future change if an audit trail of every transition is needed.
- **Reuse `PumpState` for recovery.** Rejected: conflates a UI concern with a durability concern;
  the two evolve independently.
