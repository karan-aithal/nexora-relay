/* Fake HAL for host-side unit tests: the whole HAL is the test seam (hal.h), so tests link
 * this instead of hal_host.c and get deterministic time, a scripted RX stream, and a
 * captured TX stream — no sockets, no real clock. */
#ifndef OFP_FAKE_HAL_H
#define OFP_FAKE_HAL_H

#include <stddef.h>
#include <stdint.h>

void fake_hal_reset(void);
void fake_hal_set_millis(uint32_t ms);
void fake_hal_push_rx(const uint8_t *data, size_t len); /* bytes the core will read */
const uint8_t *fake_hal_tx(size_t *len); /* bytes the core has written */
void fake_hal_clear_tx(void);
int fake_hal_gpio(int line);

#endif /* OFP_FAKE_HAL_H */
