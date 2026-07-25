/* Host HAL: the portable dispenser core runs unchanged against this on a desktop/WSL2.
 *   UART  -> a TCP socket fd (set by the host executable after it accepts the manager)
 *   millis-> CLOCK_MONOTONIC (the HAL boundary itself is the test override point: unit
 *            tests link a fake HAL instead of this one, so time is fully controllable)
 *   GPIO  -> an in-memory level array
 *   timer -> a small fixed table serviced from the host run loop (no ISR, no malloc)
 *   log   -> stderr (host only; target builds must not printf, CLAUDE.md §6) */
#include "../hal.h"

#include <errno.h>
#include <stdio.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#include "hal_host.h"

#define HOST_MAX_TIMERS 4

static int g_uart_fd = -1;
static int g_gpio[8];

typedef struct
{
    uint32_t period_ms;
    uint32_t last_fire_ms;
    void (*cb)(void *ctx);
    void *ctx;
    int active;
} host_timer;

static host_timer g_timers[HOST_MAX_TIMERS];

void hal_host_set_uart_fd(int fd)
{
    g_uart_fd = fd;
}

int hal_host_gpio(int line)
{
    return (line >= 0 && line < 8) ? g_gpio[line] : 0;
}

void hal_uart_write(const uint8_t *data, size_t len)
{
    if (g_uart_fd < 0)
    {
        return;
    }
    size_t sent = 0;
    while (sent < len)
    {
        ssize_t n = send(g_uart_fd, data + sent, len - sent, MSG_NOSIGNAL);
        if (n > 0)
        {
            sent += (size_t)n;
        }
        else if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK))
        {
            continue; /* socket buffer full; brief spin — host glue only */
        }
        else
        {
            break; /* peer closed or error */
        }
    }
}

int hal_uart_read(uint8_t *buf, size_t cap)
{
    if (g_uart_fd < 0)
    {
        return -1;
    }
    ssize_t n = recv(g_uart_fd, buf, cap, 0);
    if (n > 0)
    {
        return (int)n;
    }
    if (n == 0)
    {
        return -1; /* orderly shutdown by the manager */
    }
    if (errno == EAGAIN || errno == EWOULDBLOCK)
    {
        return 0; /* nothing available; non-blocking */
    }
    return -1;
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
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint32_t)((uint64_t)ts.tv_sec * 1000u + (uint64_t)ts.tv_nsec / 1000000u);
}

void hal_timer_register(uint32_t period_ms, void (*callback)(void *ctx), void *ctx)
{
    for (int i = 0; i < HOST_MAX_TIMERS; i++)
    {
        if (!g_timers[i].active)
        {
            g_timers[i].period_ms = period_ms;
            g_timers[i].last_fire_ms = hal_millis();
            g_timers[i].cb = callback;
            g_timers[i].ctx = ctx;
            g_timers[i].active = 1;
            return;
        }
    }
}

void hal_host_service_timers(uint32_t now_ms)
{
    for (int i = 0; i < HOST_MAX_TIMERS; i++)
    {
        if (g_timers[i].active && (now_ms - g_timers[i].last_fire_ms) >= g_timers[i].period_ms)
        {
            g_timers[i].last_fire_ms = now_ms;
            g_timers[i].cb(g_timers[i].ctx);
        }
    }
}

void hal_log(const char *message)
{
    fprintf(stderr, "%s\n", message);
}
