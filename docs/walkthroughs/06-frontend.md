# Phase 6 walkthrough — Operator console, forecourt simulator, fault injection

## 1. Design rationale

Phase 6 is the visible surface, and the temptation it creates is obvious: a dashboard can look
finished while being a picture of a system rather than a window onto one. Everything in this phase
was shaped to avoid that.

The phase file lists `web/forecourt-dashboard` as the scope, but three of its deliverables cannot
be honest against the Phase 5 backend:

- the **transaction detail drawer** wants the APDU exchange and the ISO 8583 messages field by
  field — but the site controller never ran an EMV flow and threw away every encoded message;
- the **forecourt simulator panel** wants nozzle, preset, grade and flow rate — but the pumps were
  not on the orchestration path at all, and `PumpRegistry.DispensedMillilitres` was documented as
  "a presentation animation only" (ADR 0018);
- the **fault console** is explicit that "each control triggers a real fault in the running
  system, not a UI mock" — which two of its seven controls cannot be without a real pump link.

So the phase is a frontend phase with three backend prerequisites, and each got an ADR:

- **ADR 0020** — the OPT moves inside the site controller, so the card flow, tokenisation and the
  APDU trace are on the real path and the P2PE boundary becomes a method scope you can read.
- **ADR 0021** — `PumpFleet` launches one `pump_host` process per dispenser and drives it with
  OFP-1, so the volume on the grid is metered by the same portable C core that cross-compiles for
  a Cortex-M4. The firmware's host HAL gains a stdin control channel for the physical world.
- **ADR 0022** — because the dispenser can now report what it delivered, an approval becomes a
  pre-authorisation and settlement is for the delivery.
- **ADR 0023** — the rule that holds the fault console together: nothing simulates an *outcome*.
- **ADR 0024** — the client: Angular 21, signals only, and total resynchronisation on reconnect.

The console itself is an operator board, not a consumer dashboard: dark, dense, monospace
throughout so hex and decoded fields line up, state carried by colour on a tile's left edge as
well as by the word, and every pane scrolling independently so the totals bar never moves.

The payoff, and the thing to say in an interview: **open the drawer on any transaction and you are
looking at the literal bytes.** The `C:`/`R:` lines are what passed between terminal and card, the
`DE 002` row is the acquirer message decoded by the dialect table that encoded it, and the
waterfall is measured, not modelled.

## 2. The three hardest parts, line by line

### 2a. Making an injected fault produce a *real* consequence

The easy version of a fault console is a flag in the business logic:

```csharp
if (faults.ForceDecline) return Declined;   // never written here
```

That produces identical pixels and proves nothing: the decline never came from an acquirer, and
the code that handles a real one never ran. The rule in this phase is that `FaultInjector` records
an operator's *intent* and a real component on its real path honours it. Two examples show the
shape.

**Forcing an acquirer response code.** The site controller does not fabricate a `51`. It changes
which card it presents:

```csharp
// Program.cs — the PAN on field 2 is chosen per request, not fixed at construction.
builder.Services.AddSingleton(sp => new TcpHostConnection(
    host, port, clock, options, logger,
    sp.GetRequiredService<TraceStore>(),
    () => sp.GetRequiredService<FaultInjector>().ForcedResponsePan ?? options.TestPan));
```

```csharp
// TcpHostConnection.BuildFinancial
.Set(Fields.Pan, panSelector?.Invoke() ?? options.TestPan)
```

`HostResponsePans.PanFor("51", …)` returns `4000000000000051`, which is a real row in
`deploy`'s `hostrules.json`. The decline is produced by the **host simulator**, comes back as an
0210 with `DE 039 = 51`, and is decoded by the same codec as any other response — and it appears
in the trace drawer as such. This is also how an acquirer test harness actually works, which is
why the constraint (only the codes the rules define a PAN for) is the honest one rather than a
limitation.

`91` is the interesting case. There is no `91` in any response: the rule marks that PAN silent, so
the host says nothing, `TcpHostConnection` times out against `IClock.Delay`, and the controller
takes its no-response path — reversal advice, then offline mode. That is what a 91 actually
exercises on a terminal.

**Corrupting a frame CRC.** The first attempt at this was wrong in an instructive way. A decorator
over `IPumpTransport` can only reach `PumpFrame.Payload`, which is the message *body*:

```csharp
// TcpPumpTransport.SendFrameAsync
var wire = OfpWireCodec.EncodeWire(frame.Payload.Span);   // adds STX/LEN/CRC/ETX + stuffing
```

Flipping a byte in the body produces a frame with a perfectly valid CRC over corrupted data. The
firmware would accept it. That is a *data* fault, not a *line* fault, and it is not what the phase
asks for. The corruption has to happen after framing, which is why `TcpPumpTransport` gained one
optional delegate:

```csharp
if (_wireMutator is not null)
{
    wire = _wireMutator(wire);
}
```

and `PumpFleet` supplies it:

```csharp
private byte[] Corrupt(int pumpId, byte[] wire)
{
    if (!faults.ConsumeCrcCorruption(pumpId) || wire.Length < 3)
    {
        return wire;
    }

    var corrupted = (byte[])wire.Clone();
    corrupted[^2] ^= 0xFF;   // inside the CRC's coverage, just before ETX
    return corrupted;
}
```

`ConsumeCrcCorruption` is single-shot by construction — it is a `TryRemove` on a
`ConcurrentDictionary`. That matters: if the corruption were sticky the link would never recover
and the fault would be indistinguishable from a dead pump. Single-shot gives the observable the
demo wants — the firmware drops the damaged frame (`docs/protocol-pump.md` §5: bad CRC frames are
dropped, *not* NAK'd), the manager's response timeout fires, it retransmits under the same SEQ,
and the retry succeeds. `scripts/demo-06.sh` step 4 shows the authorisation completing anyway.

The third example worth knowing is the suspension gate, because of *where* it sits:

```csharp
// FaultingHostConnection.SendAsync
await faults.WaitIfSuspendedAsync(cancellationToken).ConfigureAwait(false);
```

`TransactionService` has already written `HostRequestSent` (POINT 2) when this runs, and the
request has not reached the wire. That is precisely the window where a crash leaves the host's
state genuinely unknown and recovery must send a reversal advice. "Suspend the site controller
mid-authorisation" parks the system in its hardest recovery case, on purpose. It lives in a
decorator, so `TransactionService` contains no notion that fault injection exists.

### 2b. A trace assembled from two halves that do not share a transaction id

The drawer's data comes from components that never meet and that learn the transaction id at
different times:

- the **OPT** records seven APDU exchanges *before* `TransactionService.AuthoriseAsync` has been
  called, so before a transaction id exists;
- **`TcpHostConnection`** records the ISO 8583 request and response *during* that call, keyed by
  the id it was given;
- **`PumpFleet`** records OFP-1 commands and events *after* it, keyed by the id on the pump.

The first instinct is an ambient `AsyncLocal<TraceBuilder>`, which works and is invisible magic.
The second is to thread a trace parameter through `TransactionService`, which puts a diagnostic
concern into the type that owns the money. The design here does neither. `TransactionTrace` stamps
each step with an **absolute** timestamp rather than a pre-computed offset:

```csharp
private readonly List<(DateTimeOffset At, TimeSpan Duration, string Kind, string Label,
                       string? Hex, IReadOnlyList<TraceField> Fields)> _steps = [];
```

which makes merging two half-traces a concatenation, and ordering a sort:

```csharp
var ordered = _steps.OrderBy(s => s.At).ToArray();
var start = ordered[0].At;
var steps = ordered.Select((s, i) => new TraceStep(
    i + 1, s.Kind, s.Label,
    (s.At - start).TotalMilliseconds, s.Duration.TotalMilliseconds, s.Hex, s.Fields)).ToArray();
double total = steps.Max(s => s.ElapsedMs + s.DurationMs);
```

The OPT holds its APDU steps in a local `TransactionTrace`, and once `AuthoriseAsync` returns an
id it hands them over:

```csharp
var result = await transactions.AuthoriseAsync(request, cancellationToken).ConfigureAwait(false);
traces.Attach(result.TransactionId, trace);
```

`Attach` merges into whatever the host connection already created for that id. Whichever half was
recorded first, the projection reassembles the true order — asserted by
`Merged_trace_is_ordered_by_time_not_by_merge_order`, which deliberately records the host half
first and the earlier card half second.

`TotalMs` is `max(elapsed + duration)`, not `max(elapsed)`. Without the duration term a slow final
host response — the longest bar in a typical trace — would render as a zero-width sliver at the
right edge. There is a matching floor in the drawer, because a zero-duration marker still has to
be visible:

```ts
protected widthPercent(step: TraceStep, trace: TransactionTraceView): number {
  const raw = trace.totalMs > 0 ? (step.durationMs / trace.totalMs) * 100 : 0;
  return Math.max(raw, 0.8);
}
```

Two honesty notes. The EMV kernel does not timestamp its exchanges, so `RecordApdus` lays the
seven APDUs out evenly across the *measured* duration of the card read — the ordering and the
total are true, the per-APDU split is not, and that is stated in the code rather than dressed up.
And `FLOW_UPDATE` is deliberately kept off the trace: it fires several times a second for a whole
fuelling, and a hundred identical rows would bury the exchanges that actually explain the
transaction. It belongs on the tile, where it is.

Masking is the part that must not depend on anyone's care. `Iso8583Trace.Describe` does not know
which fields are sensitive:

```csharp
string name = message.Dialect.TryGet(number, out var definition) ? definition.Name : "(undefined)";
bool sensitive = definition is { Sensitive: true };
fields.Add(new TraceField($"DE {number:D3} {name}", sensitive ? Mask(value) : value));
```

`Sensitive` is a column in the OFC-87 dialect table (`Iso8583Dialect.Ofc87`). A new sensitive field
added there is masked here the moment it is defined, with no change to this code. The raw hex is
still shown beside it, because the two answer different questions — the hex is what a logic
analyser sees, the decoded view is what it means — and for the acquirer messages the hex is
hex-of-ASCII, so a PAN is not readable in it anyway. For the card APDUs the raw hex *does* contain
a test PAN in BCD: that is inside the OPT boundary, on reserved test PANs only, and it is what a
terminal engineer needs. `OptFlowTests` asserts the decoded side never carries it.

### 2c. Resynchronising a client that cannot know what it missed

`withAutomaticReconnect` restores the *connection*. It says nothing about state, and it cannot:
the frames sent while the client was away are gone. Worse, frames can be lost even while
connected — `PerConnectionDispatcher` uses `BoundedChannelFullMode.DropOldest` precisely so a slow
dashboard cannot stall the pump pipeline (ADR 0019).

So the client is built on two rules that together make lost frames a non-event.

**Every frame is a whole, keyed entity.** `PumpUpdated` carries a complete `PumpSnapshot`, never a
delta, so applying frames is idempotent:

```ts
private applyPump(pump: PumpSnapshot): void {
  this.pumpMap.update((current) => new Map(current).set(pump.pumpId, pump));
}
```

A duplicate delivery changes nothing. A missed one costs one stale render until the next update.
With deltas, a single dropped frame would corrupt state silently and permanently.

**A reconnect replaces the world.**

```ts
connection.onreconnecting(() => this.status.set('reconnecting'));
connection.onreconnected(() => void this.resync());
```

```ts
async resync(): Promise<void> {
  if (!this.connection) { return; }
  const snapshot = await this.connection.invoke<SiteSnapshot>('GetSnapshot');
  this.applySnapshot(snapshot);
  this.status.set('connected');
}
```

`applySnapshot` rebuilds both maps, the feed, the faults and the totals from scratch. It does not
merge. Reconciling a gap you cannot see is guesswork; replacing everything is a proof. That is also
why `SiteSnapshot` was widened in this phase to carry terminals, faults and totals as well as
pumps and transactions — a snapshot that covers only part of the state leaves the rest stale
forever after a reconnect.

Testing that deterministically is what `RealtimeConnection` exists for. It is the narrow slice of
`HubConnection` this application uses, supplied through an injection token, so a test can drive
the lifecycle by hand with no server and no sockets:

```ts
hub.snapshots.push(snapshot([pump(1), pump(2)]));
await live.connect();
hub.emit('Snapshot', hub.snapshots[0]);

hub.dropLink();
expect(live.status()).toBe('reconnecting');

// While the dashboard was away, pump 2 finished a fuelling and a third pump appeared.
hub.snapshots.push(snapshot([pump(1), pump(2, { state: 'DispenseComplete' }), pump(3)]));
hub.restoreLink();
```

and then assert the client's state matches the server's, not the state it happened to be left
with. This is the same ports-and-adapters rule the backend follows (CLAUDE.md section 3), applied
to the client — mock at the port, never above it.

The last piece is the direction of data. Commands go out over REST and **do not** update local
state; the effect arrives over the feed. So there is exactly one source of truth for what a pump is
doing, and a change made by another operator or by the firmware itself reaches every open
dashboard by the same path as one made here.

## 3. What was rejected and why

- **A C# in-process pump simulator** instead of driving the real firmware. Trivially easier: no
  child processes, no sockets, no CMake in the Docker build. It also throws away the entire point
  of Phase 4 — the demonstrable claim is that the same portable C core that cross-compiles for a
  Cortex-M4 is what meters the fuel on the grid.
- **Fault flags inside `TransactionService`.** The smallest possible diff, and it puts test-only
  branches into the code that owns the money, while proving nothing about the real failure paths.
  ADR 0023.
- **Killing the host-simulator container from the dashboard** to sever the link. Genuinely real,
  and it requires handing the web tier a Docker socket — a far worse thing to grant than a child
  process. A decorator that returns silence is the same observable event.
- **Firmware in its own container.** Cleaner separation, and it makes "power-cut a pump"
  impossible without that same Docker socket.
- **A second control socket on the firmware** rather than stdin. Works, but needs a port and a
  listener per pump; stdin is already private to the parent process and behaves identically on
  Windows.
- **Deltas on the SignalR wire.** Smaller frames, and suddenly ordering matters and a dropped
  frame corrupts state silently.
- **`AsyncLocal` for the ambient trace.** Works, invisible, and impossible to reason about at a
  call site. Absolute timestamps plus an explicit `Attach` do the same job in the open.
- **RxJS subjects with the `async` pipe** for client state. The idiom this project would have used
  three years ago; CLAUDE.md specifies signals, and they compose without subscription management.
- **A separate nginx container for the SPA.** One more service and a proxy config to keep in step,
  in exchange for nothing. Serving `wwwroot` from the site controller gives one origin, no CORS,
  and a WebSocket that needs no cross-origin negotiation.
- **Angular 22.** It requires Node `^24.15.0`; this environment has 24.11. Angular 21 has every
  feature used here. ADR 0024.
- **A real advertising video.** The MP4 is 16 kB, generated by `ffmpeg` from solid colours and
  text. The phase file says do not over-invest, and a licensed video in a public repository is a
  problem to no purpose.

## 4. Self-quiz

1. The fault console can "force a decline with response code 51". Trace exactly what happens
   between clicking that control and the decline appearing in the transaction feed. At which point
   in that path is the decline *decided*, and why does it matter that it is decided there?

2. Corrupting a frame's CRC is done by a `wireMutator` on `TcpPumpTransport` rather than by a
   decorator over `IPumpTransport`. Why can a decorator over the port not do this job? What
   *would* it produce instead, and how would the firmware respond to that?

3. The OPT records APDU exchanges before a transaction id exists, and `TcpHostConnection` records
   ISO 8583 messages after it does. Describe how the two halves end up in one correctly ordered
   trace, and explain why `TraceStep.ElapsedMs` is computed at projection time rather than when
   each step is recorded.

4. A dashboard loses its connection for thirty seconds while three pumps change state. On
   reconnect it shows the correct state. Explain the two independent properties of the design that
   make that true, and describe a change to the wire format that would break it.

5. With the pump fleet running, `SettleOnDispenseComplete` is set and an approval no longer settles
   immediately. What is now in the journal while fuel is flowing, what happens if the process is
   killed at that moment, and why is `CompleteFuellingAsync` safe to call twice for the same
   transaction?

## 5. Known weaknesses

- **The EMV kernel does not consume the PIN.** The OPT correctly stops for online PIN when the
  card's CVM list asks for it, captures the digits, and encrypts a Format-0 PIN block under a
  DUKPT-derived key — but the block is not fed back into the kernel before GENERATE AC, and it is
  not placed on the host message (there is no DE 052 in the OFC-87 dialect). The key path is real
  and proven; the placement is not. Per CLAUDE.md section 11 this is stated rather than guessed.
- **APDU timings are apportioned, not measured.** The kernel does not timestamp exchanges. The
  waterfall's ordering and total for the card read are true; the split between the seven APDUs is
  even rather than actual.
- **Settlement is a local decision, not a completion message.** `CompleteFuellingAsync` settles the
  delivered value onto the queue and the journal. A real acquirer link sends a completion/advice
  message for the delivered amount; adding one needs an MTI and dialect entries the host simulator
  does not have.
- **No authorisation timeout.** A pump that is authorised and never used holds an open `Approved`
  record until an operator cancels or the process restarts. A real forecourt reverses it after a
  few minutes.
- **The trace store is volatile and bounded.** 200 transactions, in memory, lost on restart. It is
  a diagnostic view; the journal is the record of account. The drawer says so when a trace has
  been evicted.
- **`FaultInjector.ForcedResponsePan` is site-wide.** It changes the PAN on *every* subsequent
  host request, not just one pump's. Fine for a demo, wrong for a multi-lane test rig.
- **The in-process host connection produces no ISO 8583 trace.** Only `TcpHostConnection` records
  messages, because only it has any — the in-process twin returns decisions. A controller started
  without `Site:HostEndpoint` shows an empty `iso8583` tab.
- **One dark theme, no accessibility audit beyond focus rings and contrast.** No keyboard shortcuts
  for the operator panels, no screen-reader pass over the trace tables.
- **The e2e suite is one test.** It covers the happy path end to end through the real stack. The
  fault paths are covered by backend unit tests and by the demo script, not by the browser.
- **Eight firmware processes on one host.** Fine on a developer machine; a real estate would have
  eight dispensers on a serial multi-drop, and the flow-meter tick rate here is a demo setting.
