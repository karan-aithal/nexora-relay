# Phase 4 — Pump Controller Firmware and Transport Adapters

**Goal:** Real embedded C, dual-targeted so the identical source runs on a desktop
host and would compile for an STM32, driving a dispenser state machine that the C#
side talks to over three interchangeable transports.

**Read first:** `CLAUDE.md` section 6 (C standards) and section 11.

## Order of work — mandatory

**Write `docs/protocol-pump.md` first. Do not write a line of pump code until it is
complete and reviewed.** This ordering is part of the deliverable and will be visible
in the git history.

## `docs/protocol-pump.md` must specify
- Framing: `STX | LEN(2) | SEQ | CMD | PAYLOAD | CRC16-CCITT | ETX`, byte order stated,
  byte stuffing rules for STX/ETX appearing in payload
- Command set: `AUTHORISE`, `CANCEL_AUTH`, `STATUS_REQ`, `TOTALS_REQ`,
  `SUSPEND`, `RESUME`, plus unsolicited `NOZZLE_UP`, `NOZZLE_DOWN`,
  `FLOW_UPDATE`, `DISPENSE_COMPLETE`, `FAULT`
- Full state transition table for the dispenser FSM
- Timeout, retry and sequence-number rules, including duplicate-frame handling
- Error codes and recovery procedure
- A worked example: complete byte-level trace of one successful fuelling

Note in the document that this is an original protocol *inspired by* forecourt
standards such as IFSF, not an implementation of any proprietary specification.

## Firmware (`firmware/pump/`)

### Portable core (`src/`)
- Dispenser FSM matching the documented state table exactly, as an explicit transition
  table
- Frame codec with CRC16-CCITT
- Totalizer: non-resettable lifetime volume and value counters, monotonic
- Flow meter simulation: configurable pulses per litre, generating `FLOW_UPDATE`
- Preset handling: authorise up to a volume or value limit, stop at limit
- Watchdog pattern, fault latching and clearing
- **No dynamic allocation. No `printf`. No blocking calls.**

### HAL (`hal/hal.h`)
Narrow interface: `hal_uart_write`, `hal_uart_read`, `hal_gpio_set`, `hal_millis`,
`hal_timer_register`, `hal_log`.

- `hal/host/` — UART mapped to a TCP socket, `hal_millis` from the host clock but
  overridable so tests can drive time deterministically, GPIO to a state struct
- `hal/target/` — STM32 HAL call stubs. Must compile under a cross-compiler target in
  CI; will never be executed here.

### Build
CMake with a `HAL` option selecting `host` or `target`. `cppcheck` in CI. The host
build must run as a normal executable under a debugger and in WSL2.

## C# side

### `OpenForecourt.PumpManager`
- One logical session per pump, up to 8 pumps concurrently
- **One `Channel<T>` per pump for inbound frames, one for outbound commands.** No locks
  around shared mutable pump state.
- `CancellationToken` propagated through every path; clean shutdown with no orphaned
  tasks — assert this in a test
- Reconnect with exponential backoff and jitter
- Command/response correlation by sequence number, with timeout and retry per the spec
- A mirrored C# copy of the dispenser FSM for validation, so illegal transitions
  reported by firmware are detected as protocol violations

### Transport adapters
- `TcpPumpTransport` — to the firmware host build
- `SerialPumpTransport` — real `SerialPort` over a `com0com` pair, Windows only
- `InProcPumpTransport` — in-memory frame pipe for CI

## Tests
- C: FSM transition tests for every documented transition plus every illegal one
  (Unity or a minimal assert harness)
- C: CRC and framing tests including byte-stuffing edge cases
- C#: codec tests mirroring the C tests against the same golden frames — this catches
  cross-language disagreement, which is the classic embedded integration bug
- C#: 8 concurrent pumps under load, asserting no cross-talk and clean shutdown
- Deterministic-time tests for timeout and retry using `FakeClock`

## Demo — `scripts/demo-04.ps1`
Launch 4 firmware host processes, connect the pump manager over TCP, run a full
fuelling on two pumps concurrently, print the live frame trace both directions, then
demonstrate a mid-dispense nozzle drop and a comms timeout with recovery.
