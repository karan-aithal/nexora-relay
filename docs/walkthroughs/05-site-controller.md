# Phase 5 walkthrough — Site controller: journal, offline mode, dispatch, real-time

## 1. Design rationale

Phase 5 is the orchestration tier and the phase that is really about **failure paths**. The
site controller is an ASP.NET Core host that ties the ports together: a crash-safe SQLite
journal, a TCP link to the acquiring host, RabbitMQ store-and-forward, a SignalR feed, and the
offline-authorisation logic that keeps a forecourt selling fuel when the host is unreachable.

The shape follows CLAUDE.md:

- **Everything is a port with a real in-process twin.** `ITransactionJournal`
  (`SqliteJournal` / `InMemoryJournal`), `ITransactionDispatch` (`RabbitMqDispatch` /
  `InProcDispatch`), `IHostConnection` + `IHostProbe` (`TcpHostConnection` /
  `InProcHostConnection`) — selected by configuration (ADR 0017, 0018). CI runs the whole
  failure-path suite on Linux with no broker and no host; the demo runs the real thing under
  `docker compose`.
- **The orchestrator owns the money and is free of ASP.NET.** `TransactionService` holds the
  entire lifecycle — intent, host, approve, settle, decline, reverse, recover, replay — with no
  web or SignalR types, so every failure path is a plain unit test against the in-process ports.
- **The single durability invariant:** journal the intent *before* the host request goes out,
  and journal every step before its externally-observable side effect (ADR 0016). Recovery is
  then just a switch over the status a transaction was left in.
- **Cross-cutting concerns that cannot be bypassed:** PAN masking at the log sink, per-connection
  backpressure so a slow dashboard cannot stall the pipeline (ADR 0019), idempotency keys on
  state-changing endpoints, a correlation id flowing OPT→host, RFC 7807 problem details, health
  checks, OpenTelemetry traces, and a committed OpenAPI document.

The payoff, shown live by `scripts/demo-05.sh`: four pumps authorise online; the host is killed
and authorisations keep flowing offline under the floor limit; the site controller is killed
mid-run and recovers its in-flight transactions on restart; the host returns and the offline
transactions replay and reconcile — every step observable in the totals endpoint.

## 2. The three hardest parts, line by line

### 2a. Write-intent-before-send and the five-point recovery

`TransactionService.AuthoriseAsync` writes the intent and only then touches the host:

```csharp
var tx = TransactionContext.NewIntent(Guid.CreateVersion7(), request.PumpId, request.Amount, ...);
pumps.SetState(request.PumpId, PumpState.Authorising, tx.TransactionId, request.Amount);
await WriteAsync(tx, cancellationToken); // POINT 1: intent durable before any host contact

if (!availability.IsAvailable)
    return await AuthoriseOfflineAsync(tx, cancellationToken);

tx = tx with { Status = TransactionStatus.HostRequestSent, Stan = stans.Next(), ... };
await WriteAsync(tx, cancellationToken); // POINT 2: request-sent recorded before the wire
var response = await host.SendAsync(new FinancialRequest(tx.TransactionId, tx.Amount), ...);
```

Each `WriteAsync` is a durable, idempotent upsert. The five kill points are the five places a
crash can land: after POINT 1 (intent), after POINT 2 (sent), after the `Approved` write
(POINT 3), and after the `Completed` write inside `SettleAsync` (POINT 5); the `Declined` and
`Reversed` writes are the terminal branches. On restart, `RecoveryService` reads the
non-terminal set and `ResumeAsync` resolves each:

```csharp
case TransactionStatus.Intent:          // nothing sent -> void
case TransactionStatus.HostRequestSent: // host state UNKNOWN -> reverse
case TransactionStatus.Approved when tx.Offline: // awaiting replay -> re-track exposure, leave
case TransactionStatus.Approved:        // online, never settled -> resume settlement
```

The subtle line is the `HostRequestSent` case. A crash here means the request went out but no
response was recorded — the host *might* have authorised. Treating that as approved would be a
phantom charge; treating it as declined could drop a real authorisation. The only safe move is a
**reversal advice**: tell the host to unwind whatever it may have done. That is why POINT 2 is
journalled *before* `SendAsync`, not after — without it, a crash here would leave no record at
all. The test seeds the journal in each status, reopens the same SQLite file (the crash model),
runs recovery and asserts; a companion test proves a real approval journals `Intent →
HostRequestSent → Approved → Completed` in order, so the points are genuine.

### 2b. Offline authorisation and exactly-once replay

When the prober has marked the host down, `AuthoriseOfflineAsync` decides locally:

```csharp
if (tx.Amount.Minor <= options.FloorLimitMinor && offlinePolicy.TryReserve(tx.Amount.Minor))
    return await CompleteApprovalAsync(tx, "OFFLINE", offline: true, ct); // Approved + Offline, NOT settled
```

An offline approval is journalled `Approved, Offline=true` and *not* settled — it is left on the
journal as its own durable queue. `OfflinePolicy.TryReserve` enforces both the per-transaction
floor limit and the total exposure ceiling in one lock, and exposure is rebuilt from the journal
on restart so a crash never loses count.

When `HostProber` sees the host return, the down→up edge fires `CameOnline`, which wakes
`OfflineReplayService.DrainAsync`:

```csharp
foreach (var tx in incomplete.Where(r => r.Offline && r.Status == TransactionStatus.Approved)
                             .OrderBy(r => r.CreatedAt))
    await transactions.ReplayOfflineAsync(tx, ct);
```

`ReplayOfflineAsync` sends the real host request; approved → settle → `Completed`, declined →
reversal/exception → `Reversed`. The exactly-once guarantee is structural, not a lock: the host
dedupes on transaction id, and a replayed record is driven to a **terminal** status, so a crash
mid-drain simply finds fewer records next time and the host returns the same decision it already
gave. The test runs 20 offline authorisations, brings the host back, drains, and asserts 20
distinct host requests, 20 completed, exposure zero — then drains a second time and asserts the
host request count is unchanged.

### 2c. Per-connection drop-oldest backpressure

`PerConnectionDispatcher` is the answer to "a slow dashboard must never block the pump
pipeline". Each connection gets its own bounded channel and its own send loop:

```csharp
var channel = Channel.CreateBounded<Frame>(
    new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
...
public void Broadcast(Frame frame)
{
    foreach (var connection in _connections.Values)
        connection.Channel.Writer.TryWrite(frame); // DropOldest => always succeeds, never blocks
}
```

`Broadcast` runs on the producer thread (a pump-state or transaction event) and only ever calls
`TryWrite` on a drop-oldest channel, so it *cannot* block and *cannot* fail — a wedged client's
channel silently discards its stalest frame. The client's own `PumpAsync` loop awaits the real
`SendAsync`; if it stalls, only that loop stalls. Because frames can be dropped, correctness
lives in `SnapshotProvider`: on connect and on reconnect the client pulls an authoritative
snapshot from the journal and reconciles. The test wires a `send` delegate that blocks forever
for a "slow" connection, floods 5000 frames, and asserts the flood returns in well under a
second while the "fast" connection still receives frame 5000.

## 3. What was rejected and why

- **Writing the journal after the host responds** — loses a possibly-authorised sale on a crash
  between send and response. Rejected; intent-first is the whole point (ADR 0016).
- **Wiring `RabbitMQ.Client` directly into the orchestrator** — breaks CI-on-Linux-without-a-broker
  and the mock-at-the-port rule. Rejected in favour of `ITransactionDispatch` (ADR 0017).
- **`Clients.All.SendAsync` with a per-send timeout** — still couples the producer to the slowest
  client and drops the newest frame, not the stalest. Rejected for per-connection drop-oldest
  (ADR 0019).
- **A Serilog enricher for PAN masking** — runs before rendering and is bypassable; masking the
  rendered output at the sink is the only bypass-proof point (ADR 0019).
- **Full card→OPT→pump→host wiring in Phase 5** — scope creep over Phases 6–7 that would bury the
  failure-path work. Deferred; the orchestration is ports-centric (ADR 0018).

## 4. Self-quiz

1. A crash happens after the host request is sent but before a response is read. Why is a
   reversal the only correct recovery, and what would go wrong if you treated it as a decline or
   as an approval?
2. Offline replay is "exactly once" without any distributed lock. Which two independent
   mechanisms combine to guarantee that, and where would the guarantee break if you removed
   either one?
3. `Broadcast` never blocks even when a client is completely wedged. What data-structure choice
   makes that true, and what is the price paid for it — and why is that price acceptable here?
4. PAN masking is applied at the sink after rendering rather than at the call site or in an
   enricher. Give a concrete log call that a call-site or enricher approach would leak but the
   sink approach catches.
5. The offline exposure ceiling is rebuilt from the journal on startup. Walk through what would
   go wrong on a restart-during-outage if it were not, using the demo's three offline
   transactions as the example.

## 5. Known weaknesses (simplifications relative to production)

- **Settlement at authorisation time.** A real forecourt pre-authorises a floor amount, dispenses,
  then captures the actual litres. Here the requested amount is authorised and settled in one
  step; the dispense animation is a Phase-6 concern. The failure-path machinery is unaffected.
- **In-memory idempotency store.** Keyed results live for the process lifetime with no TTL and no
  sharing — fine for a single site controller, but a multi-instance deployment would need a shared
  store, and a long-running process would want eviction.
- **`synchronous=NORMAL` WAL.** Durable across process crash (the kill-point model) but can lose
  the last commit on a hard power cut without a checkpoint; `FULL` is the upgrade path.
- **The host link carries a single site test PAN.** In production a payment gateway detokenizes the
  card token at the acquirer boundary; this simulation stands in for that exchange with one
  reserved test PAN, so no real detokenization service exists yet.
- **Availability is a TCP-connect probe**, not an ISO 8583 `0800` echo. It detects a dead link but
  not a host that accepts connections while failing to authorise; a network-management echo would
  be more faithful.
- **The pump firmware is not on this path.** `docker compose up` brings up the site controller,
  host simulator and RabbitMQ; the firmware has its own host build and demo (Phase 4).
