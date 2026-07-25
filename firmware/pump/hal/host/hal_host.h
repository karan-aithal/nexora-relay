/* Host-only HAL extensions used by the host executable (main_host.c). Not part of the
 * portable HAL interface (hal.h) — these wire the simulated UART to a real socket and
 * service the timer table from the run loop. */
#ifndef OFP_HAL_HOST_H
#define OFP_HAL_HOST_H

#include <stdint.h>

/* Bind the simulated UART to an accepted TCP socket (or -1 to detach on disconnect). */
void hal_host_set_uart_fd(int fd);

/* Read back a GPIO line level (for host-side logging / assertions). */
int hal_host_gpio(int line);

/* Fire any due registered timers; call once per run-loop iteration. */
void hal_host_service_timers(uint32_t now_ms);

#endif /* OFP_HAL_HOST_H */
