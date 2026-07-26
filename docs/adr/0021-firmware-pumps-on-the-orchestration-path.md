# 0021 — The pump firmware is on the orchestration path, and the site controller owns the physical world

Supersedes the scope note in [0018](0018-offline-authorisation-and-orchestration-scope.md) that
kept the pump link off the Phase 5 orchestration path.

## Context

Phase 5 deliberately left the dispensers out: `PumpRegistry.DispensedMillilitres` was documented
as "a presentation animation only", and the Phase 4 firmware was driven by its own demo
(`scripts/demo-04`). That was the right call for a phase about failure paths in the money
lifecycle.

Phase 6 makes it untenable. Three of its fault-injection controls — drop the nozzle mid-dispense,
corrupt a pump frame CRC, power-cut a pump mid-transaction — are meaningless without a real pump
link, and the phase is explicit that "each control triggers a real fault in the running system,
not a UI mock". A grid showing animated numbers would be a demonstration of nothing.

## Decision

**`Simulation/PumpFleet` launches one `pump_host` process per dispenser and drives it with OFP-1
over `TcpPumpTransport` and `PumpSession`.** The volume and value on the dashboard are metered by
firmware and carried over a framed, CRC-checked, sequence-correlated protocol.

Three supporting decisions follow from it:

- **The site controller owns the firmware processes.** Not a separate container: "power-cut a
  pump" has to mean killing a process, and only the parent can do that. The Docker image builds
  the firmware in its own stage and copies the binary in.
- **The physical world is driven over the firmware's stdin.** `pump_host` gains a newline-delimited
  control channel (`up`, `down`, `flow N`, `drop N`, `price N`) which is only consulted under
  `--manual 1`. Every command lands on the same `ofp_pump_nozzle_up` / `_on_pulses` entry points a
  sensor ISR calls. It is a second source of *physical input*, never a back door into the
  protocol, and the firmware core is untouched — the channel lives entirely in the host HAL, which
  is already where the simulated customer lives.
- **CRC corruption is a wire-level hook.** `TcpPumpTransport` gains an optional `wireMutator`
  applied to the fully framed bytes immediately before the socket write. Corrupting the *body*
  would produce a valid CRC over corrupted data; corrupting after framing is what makes the
  firmware's own CRC check reject the frame and the manager retransmit under its own timeout
  (`docs/protocol-pump.md` §4.2, §5).

**The fleet is optional.** With `Site:PumpFirmwarePath` unset it does not start, and the Phase 5
behaviour is unchanged — which is how CI and every existing unit test keep running with no
firmware binary present.

## Consequences

- The dashboard's flagship numbers are real. So is the whole class of pump fault the phase asks
  for, including the interesting one: a corrupted frame is *dropped*, not NAK'd, so what the
  operator sees is a retransmission and then success.
- The site controller now spawns child processes and must reap them. `PumpFleet` kills the whole
  process tree on shutdown and on an injected power cut.
- Because the pump can now report what it delivered, an approval becomes a **pre-authorisation**
  (see [0022](0022-preauthorisation-and-settlement-on-delivery.md)).
- The Docker image is larger and its build is slower: it now compiles C and TypeScript as well as
  C#. Three build stages, one runtime image.
- Eight firmware processes plus eight TCP links is real concurrency on the demo machine. The
  `--tick-ms` and `--flow-ml-per-tick` settings keep the frame rate sane.

## Alternatives considered

- **A C# in-process pump simulator.** Trivial to write, no processes, no sockets. It would also
  throw away the entire point of Phase 4: the thing being demonstrated is that the same portable
  C core that cross-compiles for a Cortex-M4 is the thing driving the forecourt.
- **Firmware in its own container, reached over TCP only.** Cleaner separation, and it makes
  "power-cut a pump" impossible from inside the site controller without a Docker socket — which is
  a far worse thing to hand a web tier than a child process.
- **A second control socket on the firmware instead of stdin.** Works, but it needs a second port
  per pump and a second listener in the C. Stdin is already there, already private to the parent
  process, and works identically on Windows.
