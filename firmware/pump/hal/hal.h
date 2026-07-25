/* Hardware Abstraction Layer — the single narrow seam between the portable dispenser core
 * (firmware/pump/src) and the platform. Two implementations exist:
 *   hal/host/   — UART mapped to a TCP socket, hal_millis from the host clock (overridable
 *                 for deterministic tests), GPIO to an in-memory struct.
 *   hal/target/ — STM32 HAL call stubs; compiled by CI under a cross-compiler, never run here.
 * The portable core calls ONLY these functions for I/O; nothing else platform-specific
 * leaks in. This is the embedded mirror of the C# ports-and-adapters seam (CLAUDE.md §3). */
#ifndef OFP_HAL_H
#define OFP_HAL_H

#include <stddef.h>
#include <stdint.h>

/* GPIO line identifiers (outputs the dispenser drives). */
#define OFP_GPIO_VALVE 0 /* fuel valve / pump motor enable */
#define OFP_GPIO_FAULT_LAMP 1 /* fault indicator lamp */

/* Write bytes to the controller link (UART on target, TCP socket on host). Non-blocking on
 * the host: buffered and flushed by the platform. */
void hal_uart_write(const uint8_t *data, size_t len);

/* Read up to cap available bytes without blocking. Returns the count read (0 if none), or
 * -1 if the link has closed. The core polls this; it never blocks in the run loop. */
int hal_uart_read(uint8_t *buf, size_t cap);

/* Drive a GPIO output line to level (0/1). */
void hal_gpio_set(int line, int level);

/* Monotonic milliseconds since boot. Overridable on the host so tests can drive time. */
uint32_t hal_millis(void);

/* Register a periodic callback (used by the host to simulate the flow-meter pulse tick).
 * On the target this maps to a hardware timer ISR. */
void hal_timer_register(uint32_t period_ms, void (*callback)(void *ctx), void *ctx);

/* Diagnostic log line. The only logging path: target builds route it to RTT/UART, never
 * printf (CLAUDE.md §6 C rules). */
void hal_log(const char *message);

#endif /* OFP_HAL_H */
