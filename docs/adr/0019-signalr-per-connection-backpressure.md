# 0019 — SignalR backpressure via per-connection drop-oldest channels, and PAN masking at the sink

## Context

Two Phase-5 cross-cutting requirements both have a "cannot be bypassed" character. First: a slow
dashboard client must never block the pump pipeline. SignalR's default `Clients.All.SendAsync`
awaits every client, so one stalled browser back-pressures the producer. Second: PAN masking must
be enforced by a test, not by discipline (CLAUDE.md section 7.3) — no log call anywhere may leak a
PAN.

## Decision

- **`PerConnectionDispatcher`: one bounded, drop-oldest channel and one dedicated send loop per
  connection.** Broadcasting only ever calls `TryWrite` on `BoundedChannelFullMode.DropOldest`
  channels, so it returns immediately and never awaits client I/O. A client that cannot keep up
  loses its *stalest* frames — acceptable for a live view — while its send loop drains at its own
  pace. The engine is deliberately free of SignalR types (the per-connection send is an injected
  delegate), so the backpressure behaviour is unit-tested with a deliberately wedged sender.
- **Reconnection resynchronises from an authoritative snapshot.** Because intermediate frames can
  be dropped, correctness cannot depend on the live stream. On connect (and via a `GetSnapshot`
  hub method after a reconnect) the client is sent the full pump + recent-transaction snapshot
  built from the journal, and reconciles from there.
- **PAN masking at the Serilog sink.** `PanMaskingSink` renders each event, runs
  `PanMasker.Mask` (first-6 + last-4, middle starred) over the rendered text, and only then
  writes to the inner writer. Masking after rendering means *whatever* a careless caller logs —
  message, structured property, any level — the bytes leaving the sink are masked. It is wired as
  the only sink, so it cannot be bypassed.

## Consequences

- A test floods 5000 frames at a wedged "slow" client and asserts broadcasting stays under budget
  while a "fast" client still receives the final frame — the pipeline never stalls.
- A test logs reserved test PANs at every level, as message and as property, and asserts no
  unmasked PAN reaches the sink writer while the masked form does.
- Dropped frames are invisible to the user because every (re)connection resyncs to journal truth.

## Alternatives considered

- **`Clients.All.SendAsync` with a send timeout.** Rejected: still couples producer latency to the
  slowest client, and a timeout drops the *newest* frame, not the stalest.
- **A Serilog enricher for masking.** Rejected: an enricher runs before rendering and can be
  bypassed by properties it does not know to inspect; masking the rendered output at the sink is
  the only bypass-proof point.
- **Unbounded per-connection queues.** Rejected: a permanently slow client would grow memory
  without bound; drop-oldest caps it and the snapshot makes the drop safe.
