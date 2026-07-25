#include "fake_hal.h"

#include <string.h>

#include "../hal/hal.h"

#define RX_CAP 4096
#define TX_CAP 8192

static uint8_t g_rx[RX_CAP];
static size_t g_rx_len;
static size_t g_rx_pos;

static uint8_t g_tx[TX_CAP];
static size_t g_tx_len;

static uint32_t g_millis;
static int g_gpio[8];

void fake_hal_reset(void)
{
    g_rx_len = 0;
    g_rx_pos = 0;
    g_tx_len = 0;
    g_millis = 0;
    memset(g_gpio, 0, sizeof g_gpio);
}

void fake_hal_set_millis(uint32_t ms)
{
    g_millis = ms;
}

void fake_hal_push_rx(const uint8_t *data, size_t len)
{
    if (g_rx_len + len > RX_CAP)
    {
        len = RX_CAP - g_rx_len;
    }
    memcpy(g_rx + g_rx_len, data, len);
    g_rx_len += len;
}

const uint8_t *fake_hal_tx(size_t *len)
{
    *len = g_tx_len;
    return g_tx;
}

void fake_hal_clear_tx(void)
{
    g_tx_len = 0;
}

int fake_hal_gpio(int line)
{
    return (line >= 0 && line < 8) ? g_gpio[line] : 0;
}

/* ---- hal.h implementation ---- */

void hal_uart_write(const uint8_t *data, size_t len)
{
    if (g_tx_len + len > TX_CAP)
    {
        len = TX_CAP - g_tx_len;
    }
    memcpy(g_tx + g_tx_len, data, len);
    g_tx_len += len;
}

int hal_uart_read(uint8_t *buf, size_t cap)
{
    size_t avail = g_rx_len - g_rx_pos;
    if (avail == 0)
    {
        return 0;
    }
    size_t take = avail < cap ? avail : cap;
    memcpy(buf, g_rx + g_rx_pos, take);
    g_rx_pos += take;
    return (int)take;
}

void hal_gpio_set(int line, int level)
{
    if (line >= 0 && line < 8)
    {
        g_gpio[line] = level ? 1 : 0;
    }
}

uint32_t hal_millis(void)
{
    return g_millis;
}

void hal_timer_register(uint32_t period_ms, void (*callback)(void *ctx), void *ctx)
{
    (void)period_ms;
    (void)callback;
    (void)ctx; /* tests drive flow directly via ofp_pump_on_pulses */
}

void hal_log(const char *message)
{
    (void)message;
}
