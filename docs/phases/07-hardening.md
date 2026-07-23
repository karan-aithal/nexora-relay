# Phase 7 — Failure Paths, Reconciliation, E2E Suite, Documentation

**Goal:** Turn a working demo into something that reads as production-minded
engineering. The happy path is already done; this phase is entirely about what happens
when things go wrong.

## In scope
Cross-cutting hardening, chaos testing, all documentation, interview preparation.

## Deliverables

### The reversal matrix
Document and implement every case in `docs/reversal-matrix.md`:

| Scenario | Correct behaviour |
|---|---|
| Host timeout, no response | Reversal, retry with backoff until acknowledged |
| Late host response after timeout | Reconcile; do not double-charge |
| Nozzle dropped after auth, zero volume | Reversal for full pre-auth |
| Partial dispense | Completion for actual amount, not pre-auth |
| Pump power loss mid-dispense | Recover totals on restart, complete or reverse |
| Site controller crash after auth, before dispense | Journal recovery, reverse |
| Site controller crash mid-dispense | Recover from totalizer, complete |
| Card removed during GENERATE AC | Abort cleanly, no host message sent |
| Duplicate STAN from a retry | Host duplicate detection, single charge |
| Reversal itself times out | Persistent retry queue, alert after N attempts |

Every row needs a test.

### End-of-day reconciliation
- Batch close, settlement totals by card brand and by pump
- Three-way reconciliation: pump totalizers vs journal vs host ledger
- Discrepancy report showing exactly where a mismatch arose

### Chaos end-to-end suite
- 200+ transactions across 8 pumps with randomised fault injection
- Invariants asserted after every run: no lost transaction, no double charge, journal
  totals equal host ledger totals, every reversal accounted for
- Seeded random so failures are reproducible; the seed goes in the failure output
- Runs in CI via Docker Compose, on a schedule rather than every push

### Performance
- Latency budget documented and measured: card tap to authorisation displayed
- Throughput under 8 concurrent pumps
- Verify no unbounded queue growth under sustained load

### Documentation
- `README.md` rewritten as a design document: problem, architecture, C4 diagrams in
  Mermaid, how to run, what is simulated and what is not
- Architecture diagrams: system context, container, and the transaction sequence
- `docs/whats-not-real.md` — an honest inventory of every simplification, and what
  would differ in production. This section earns more credibility than any feature.
- 5-minute demo video script

### `docs/interview-prep.md`
Ten war stories, each anchored to a specific file and line:
concurrency bug found and how; a reversal edge case that changed the design; a key
management decision and its tradeoff; a cross-language protocol disagreement between
the C and C# codecs; a crash-recovery scenario that revealed a wrong write ordering;
and five more drawn from what actually happened during the build.

For each: the situation, what was tried, what the resolution was, and what would be
done differently.

## Definition of Done
All of `CLAUDE.md` section 9, plus: the chaos suite runs green three consecutive times
with different seeds, and the README makes sense to someone who has never seen the
repository.
