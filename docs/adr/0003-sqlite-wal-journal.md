# ADR-0003 — SQLite (WAL mode) for the transaction journal

## Status
Accepted (Phase 0). Interface only in this phase; implementation lands when transactions
are first persisted.

## Context
A forecourt terminal can lose power mid-transaction — during authorisation, mid-dispense,
or between dispense and settlement. On restart the system must know which transactions
were in flight and resolve them (reverse, complete, or flag). This demands a **crash-safe,
locally-durable** record with well-defined write ordering. It is a device-appropriate
persistence problem, not a server database problem.

## Decision
The `ITransactionJournal` port is backed in production by **SQLite in WAL
(write-ahead logging) mode**. WAL gives durable, atomic commits and lets a reader see a
consistent snapshot while a writer appends. On restart, `ReadIncompleteAsync` returns the
latest state of every non-terminal transaction for recovery.

An in-memory journal implements the same port for tests.

## Consequences
- **Positive:** Survives power loss; a committed `AppendAsync` is durable. Single-file,
  zero-admin, embeddable — correct for a device.
- **Positive:** WAL improves concurrency of the append/read pattern the journal uses.
- **Negative:** Not a networked/multi-writer store. That is a non-goal: the journal is
  local to a controller.
- **Note:** Write-ordering and crash-recovery logic is one of the three pieces flagged in
  KICKOFF.md to own by hand; the durability guarantee lives behind this port so tests can
  assert it.

## Alternatives considered
- **PostgreSQL / any server RDBMS.** Rejected (CLAUDE.md section 5): wrong shape for an
  embedded device; adds an external dependency and admin surface for no benefit here.
- **Append-only flat file with fsync.** Rejected: we would re-implement atomic commit and
  crash recovery that SQLite WAL already provides correctly.
