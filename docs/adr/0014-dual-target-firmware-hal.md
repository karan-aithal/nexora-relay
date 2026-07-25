# 0014 — Dual-target firmware behind a narrow HAL

## Context

CLAUDE.md requires "real-time embedded development in C, with hardware abstraction", and the
same portable core to run on a desktop host and to compile for an STM32 that is never executed
here. The C rules (§6) are strict: C11, `-Wall -Wextra -Werror -pedantic`, **no dynamic
allocation after init**, **no `printf` in target code**, no blocking in the run loop, and state
machines as explicit tables. This is the embedded mirror of the ports-and-adapters thesis (§3).

## Decision

- **One portable core (`firmware/pump/src/`) that talks only through a six-function HAL**
  (`hal_uart_write/read`, `hal_gpio_set`, `hal_millis`, `hal_timer_register`, `hal_log`). The core
  contains no platform code.
- **Two HAL implementations selected by a CMake `HAL` option:** `host/` maps the UART to a TCP
  socket, `hal_millis` to `CLOCK_MONOTONIC`, GPIO to a struct, and services a small timer table
  from the run loop; `target/` binds the identical core to the STM32Cube HAL. The target build is
  a `STATIC_LIBRARY` compiled under `arm-none-eabi-gcc` (`cmake/arm-none-eabi.cmake`,
  `TRY_COMPILE_TARGET_TYPE=STATIC_LIBRARY`) — **archived compile-only, never linked or flashed**,
  which is the portability proof.
- **The dispenser FSM is a pure `[state][event]` table** (`fsm.c`) with no I/O, so every cell is
  unit-testable directly and the C# side can mirror it exactly.
- **No dynamic allocation:** fixed frame buffers, a fixed timer table, a fixed idempotency cache.
  Logging is only `hal_log` (host → stderr, target → SWO/ITM), never `printf`.
- **The test HAL is the seam:** unit tests link a fake HAL for deterministic time and a scripted
  RX/TX stream, so `hal_millis` is "overridable" at the boundary rather than via a magic global.

## Consequences

- The same `.c` files that run the host demo would compile for the STM32; CI proves both the host
  build (run + tested) and the ARM cross-compile (archived), and keeps `cppcheck` clean.
- Swapping simulation for hardware is a HAL/CMake choice, not a core change — the identical thesis
  as the C# adapters.
- The STM32 HAL signatures live in a small shim (`stm32_hal_shim.h`) marked `SPEC-UNVERIFIED`
  because exact Cube signatures vary by family/version; on device the vendor headers replace it.

## Alternatives considered

- **A single host-only implementation.** Rejected: it would not demonstrate portability or the HAL
  discipline the role asks for, and "no `printf`/no `malloc`" would be untested aspirations.
- **Unity or CMocka for the C tests.** Rejected as an unneeded dependency; a ~40-line assert
  harness meets "meaningful unit tests" without a framework.
- **A table-driven FSM *engine*.** Rejected as over-engineering (mirrors ADR 0009): a plain 2-D
  table literal *is* the explicit machine and reads as the documented transition table.
