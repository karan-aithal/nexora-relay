# 0024 — Angular 21, signals-only state, and total resynchronisation on reconnect

## Context

The operator console has to hold live state for eight pumps, eight terminals, a transaction feed,
the armed fault set and site totals, updated over SignalR, and stay correct across a dropped
connection. CLAUDE.md section 6 requires standalone components, signals for state, strict
templates and no `any`.

## Decision

- **Angular 21, not 22.** Angular 22 requires Node `^24.15.0`; the development environment runs
  Node 24.11. 21 is the latest stable line that runs here, and it has everything this console
  uses: standalone components by default, signal `input()`/`output()`, `@if`/`@for` control flow,
  and zoneless change detection.
- **Zoneless (`provideZonelessChangeDetection`).** Every piece of state is a signal, so zone.js
  has nothing to do but cost. Change detection is driven by signal reads, which is also what makes
  `ChangeDetectionStrategy.OnPush` on every component honest rather than decorative.
- **One connection, one state holder.** `ForecourtRealtime` owns the hub and every live signal.
  Components take state as inputs and raise outputs; none of them fetch.
- **Commands over REST, effects over the feed.** `ForecourtApi` posts and does not update local
  state. The change comes back over SignalR. There is therefore exactly one source of truth for
  what a pump is doing, and a change made by another operator — or by the firmware itself —
  reaches every open dashboard by the same path.
- **Every frame is a whole, keyed entity.** `PumpUpdated` carries a complete `PumpSnapshot`, not a
  delta, so applying frames is idempotent and a duplicate delivery after a reconnect is harmless.
- **A reconnect always resynchronises, wholesale.** On `onreconnected` the client invokes
  `GetSnapshot` and replaces everything it holds. It cannot know which frames it missed while it
  was away — and with `PerConnectionDispatcher` dropping the oldest frames under backpressure
  ([0019](0019-signalr-per-connection-backpressure.md)), it cannot know that even while connected.
  Replacing the world is correct; reconciling a gap you cannot see is guesswork.
- **The connection is a port.** `RealtimeConnection` is the narrow slice of `HubConnection` this
  application uses, supplied through an injection token. That is the same ports-and-adapters rule
  the backend follows, applied to the client, and it is what lets the reconnection tests drive the
  lifecycle by hand against a stub hub with no server and no sockets.

## Consequences

- The resynchronisation behaviour is unit-tested deterministically: drop the link, change the
  world behind the client's back, restore it, and assert the client's state matches the server's.
- The snapshot carries *everything* the feed can push, which makes it a slightly heavier payload
  and makes correctness after a reconnect trivial to reason about. At forecourt scale that is a
  good trade.
- Totals are pushed on a `TotalsUpdated` frame when a transaction reaches a status that moves
  them, rather than recomputed on every lifecycle write.
- The console is served from the site controller's `wwwroot`, so there is one origin and no CORS
  surface; `ng serve` proxies to it in development.
- Angular must be moved to 22 when the environment's Node is upgraded. That is a version bump, not
  a rewrite.

## Alternatives considered

- **RxJS `BehaviorSubject`s with the `async` pipe.** The idiom this project would have used three
  years ago. Signals are what CLAUDE.md specifies, and they compose without subscription
  management.
- **Deltas on the wire.** Smaller frames, and ordering suddenly matters: a dropped frame corrupts
  state silently rather than costing one stale render.
- **Trusting `withAutomaticReconnect` alone.** It restores the connection, not the state. Without
  the snapshot resync a dashboard would come back showing whatever it held before the drop.
- **A separate nginx container serving the SPA.** One more service and a proxy configuration to
  keep in step, in exchange for nothing this deployment needs.
