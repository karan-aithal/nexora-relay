# 0023 — Injected faults are *armed*, never simulated

## Context

Phase 6's differentiating feature is a fault console where "each control triggers a real fault in
the running system, not a UI mock" and "every injected fault must produce a correct, observable
system response".

The easy implementation is a flag the business logic checks: `if (faultInjector.ForceDecline)
return Declined;`. It produces the same pixels and proves nothing — the decline never came from an
acquirer, the CRC error was never detected by firmware, and the code that would handle a real one
was never executed.

## Decision

**Nothing in the fault subsystem produces an outcome. `FaultInjector` records an operator's
intent, and a real component on its real path honours it.** Concretely:

| Control | What is actually done | Who produces the consequence |
|---|---|---|
| Kill the host link | `FaultingHostConnection` returns `NoResponse` without touching the socket | `HostProber` marks the host down; the site falls back to offline authorisation |
| Host latency | `IClock.Delay` before the exchange | The configured host timeout, unmodified |
| Force a response code | `panSelector` puts the test PAN the acquirer's rules decline on field 2 | The **host simulator** returns 51 / 05 / 54, or stays silent for 91 |
| Suspend mid-authorisation | A `TaskCompletionSource` gate after the `HostRequestSent` journal write, before the wire | Nothing — that is the point; the system is parked in its hardest recovery window |
| Corrupt a frame CRC | `TcpPumpTransport`'s `wireMutator` flips a byte inside the CRC's coverage | The **firmware** fails its CRC check and drops the frame; the manager retransmits |
| Drop the nozzle | `down` on the firmware's control channel | `ofp_pump_nozzle_down`, the same call a holster sensor makes |
| Power-cut a pump | `Process.Kill(entireProcessTree: true)` | The transport's reconnect loop |
| Force a duplicate | The identical `FinancialRequest` is re-sent | The **acquirer's** `(terminal, STAN)` ledger answers 94, duplicate transmission |

Two structural rules keep it that way:

- **Faults live in decorators and injected delegates, never in the orchestrator.**
  `TransactionService` has no notion that fault injection exists; what it sees is a host that
  timed out or one that took eight seconds, indistinguishable from the real thing.
- **The decorator is what the container hands out.** `IHostConnection` and `IHostProbe` both
  resolve to `FaultingHostConnection`, so there is no unwrapped path a caller could take.

## Consequences

- Every consequence on screen is the system's own. Killing the link really does drive the site
  into offline authorisation under the floor limit, with the exposure ceiling and the replay on
  restore all doing their Phase 5 jobs.
- Forcing a response code is limited to the codes `deploy`'s `hostrules.json` defines a test PAN
  for. That is a real constraint and the honest one: an acquirer test harness works exactly this
  way.
- `91` is produced by the silent-PAN rule rather than a literal 91 in a response — the host does
  not answer at all, so the controller takes its no-response path. That is what a 91 exercises on
  a terminal, and it is documented on the endpoint.
- CRC corruption is single-shot. If it were sticky the link would never recover and the fault
  would be indistinguishable from a dead pump.
- The suspension gate is cancellable, so a held authorisation cannot block shutdown.

## Alternatives considered

- **Flags checked inside `TransactionService`.** Smallest diff, and it would put test-only
  branches in the code that owns the money. Rejected outright.
- **A separate "chaos" build configuration.** Keeps the production path clean, but then the demo
  is not running the production path, which is the one thing the feature is for.
- **Killing the host-simulator container from the dashboard.** Genuinely severs the link, and it
  requires handing the web tier a Docker socket. A decorator that returns silence is the same
  observable event with none of that.
