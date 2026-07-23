# Phase 5 — Site Controller: REST, SignalR, RabbitMQ, Crash-Safe Journal

**Goal:** The orchestration tier, and the part that proves you understand that payments
engineering is about failure paths.

## In scope
`OpenForecourt.SiteController`, SQLite journal, `deploy/docker-compose.yml`

## Deliverables

### ASP.NET Core host
- Versioned REST API (`/api/v1/...`): pumps, pump detail, authorise, cancel,
  transactions, transaction detail, totals, health
- Problem Details (RFC 7807) for errors, correct status codes, idempotency keys on
  every state-changing endpoint
- OpenAPI document generated and committed
- Health checks for RabbitMQ, host link and journal
- Structured logging (Serilog) with a **PAN-masking enricher applied at sink level**, so
  masking cannot be bypassed by a careless log call
- OpenTelemetry traces, correlation id flowing from OPT through to host request

### SignalR hub
- Live pump state, dispense progress, transaction lifecycle events
- Backpressure handling — a slow dashboard client must never block the pump pipeline
- Reconnection with state resynchronisation on the client

### RabbitMQ
- Authorisation request/response exchange
- Store-and-forward queue with a durable dead-letter queue
- Consumer idempotency by transaction id

### Transaction journal (SQLite, WAL)
This is the crash-safety story. Get it right:
- **Write intent before the host request is sent**, not after
- On startup, scan for in-flight records and reconcile: query the host, or reverse
- Idempotent writes keyed by transaction id
- A test that kills the process (or simulates it) at each of five defined points in the
  transaction lifecycle and asserts correct recovery on restart

### Offline mode
- Detect host unavailability, switch to offline authorisation below a configured floor
  limit
- Queue offline transactions durably
- On reconnection, replay in order; if a replayed transaction is declined, generate the
  correct reversal or exception record
- Configurable offline transaction ceiling and total exposure limit

### Docker Compose
Site controller, RabbitMQ, host simulator, and the firmware host builds — one
`docker compose up` brings up the whole forecourt.

## Tests
- Journal crash-recovery at each defined kill point
- Offline mode: go offline, run 20 transactions, come back online, assert all replayed
  exactly once
- Idempotency: replay the same request 5 times, assert one transaction
- SignalR backpressure: slow consumer does not stall the pipeline

## Demo — `scripts/demo-05.ps1`
`docker compose up`, run transactions on 4 pumps, kill the host simulator mid-run, show
offline authorisation continuing, restore the host, show replay and reconciliation, then
kill and restart the site controller mid-transaction and show recovery.
