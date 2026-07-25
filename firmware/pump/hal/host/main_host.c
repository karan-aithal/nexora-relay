/* OFP-1 pump firmware — host executable. Runs the identical portable core (src/) that
 * would run on an STM32, but with UART mapped to a TCP socket the C# pump manager connects
 * to. It also plays the *physical world*: a scripted customer who lifts the nozzle after
 * authorisation, lets fuel flow via the flow-meter timer, and optionally holsters early.
 *
 * The firmware logic proper knows none of this — the customer script only calls the same
 * ofp_pump_nozzle_up / _on_pulses / _nozzle_down entry points a real sensor ISR would. */
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#include "../../src/pump.h"
#include "../../src/protocol.h"
#include "../hal.h"
#include "hal_host.h"

typedef struct
{
    ofp_pump pump;
    uint32_t flow_ml_per_tick;
    uint32_t drop_at_ml; /* 0 = run to preset/limit; >0 = customer holsters here */
    int dropped;
} app_t;

static volatile sig_atomic_t g_stop = 0;
static void on_sigint(int sig)
{
    (void)sig;
    g_stop = 1;
}

/* Flow-meter tick, registered with hal_timer_register. While dispensing it injects a batch
 * of pulses; if a manual early stop is configured it holsters the nozzle at that volume. */
static void flow_tick(void *ctx)
{
    app_t *app = (app_t *)ctx;
    if (app->pump.state != OFP_STATE_DISPENSING)
    {
        return;
    }
    if (app->drop_at_ml > 0 && app->pump.dispense_ml >= app->drop_at_ml && !app->dropped)
    {
        app->dropped = 1;
        ofp_pump_nozzle_down(&app->pump);
        return;
    }
    uint32_t pulses = app->flow_ml_per_tick * app->pump.cfg.pulses_per_litre / 1000u;
    ofp_pump_on_pulses(&app->pump, pulses);
}

static int arg_u32(int argc, char **argv, const char *name, uint32_t def, uint32_t *out)
{
    *out = def;
    for (int i = 1; i < argc - 1; i++)
    {
        if (strcmp(argv[i], name) == 0)
        {
            *out = (uint32_t)strtoul(argv[i + 1], NULL, 10);
            return 1;
        }
    }
    return 0;
}

static int make_listener(uint16_t port)
{
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0)
    {
        return -1;
    }
    int yes = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof yes);
    struct sockaddr_in addr;
    memset(&addr, 0, sizeof addr);
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = htons(port);
    if (bind(fd, (struct sockaddr *)&addr, sizeof addr) < 0 || listen(fd, 1) < 0)
    {
        close(fd);
        return -1;
    }
    return fd;
}

int main(int argc, char **argv)
{
    uint32_t port, price, ppl, flow_ml, tick_ms, nozzle_delay, drop_at, manual;
    if (!arg_u32(argc, argv, "--port", 0, &port) || port == 0)
    {
        fprintf(stderr, "usage: %s --port N [--price mpl] [--pulses-per-litre N] "
                        "[--flow-ml-per-tick N] [--tick-ms N] [--nozzle-delay-ms N] "
                        "[--drop-at-ml N] [--manual 1]\n",
                argv[0]);
        return 2;
    }
    arg_u32(argc, argv, "--price", 150, &price); /* minor units per litre */
    arg_u32(argc, argv, "--pulses-per-litre", 1000, &ppl);
    arg_u32(argc, argv, "--flow-ml-per-tick", 100, &flow_ml);
    arg_u32(argc, argv, "--tick-ms", 100, &tick_ms);
    arg_u32(argc, argv, "--nozzle-delay-ms", 300, &nozzle_delay);
    arg_u32(argc, argv, "--drop-at-ml", 0, &drop_at);
    arg_u32(argc, argv, "--manual", 0, &manual);

    signal(SIGINT, on_sigint);
    signal(SIGTERM, on_sigint);

    ofp_pump_config cfg = {
        .unit_price_per_litre = price,
        .pulses_per_litre = ppl,
        .t_event_repeat_ms = 1000,
        .t_stall_ms = 3000,
        .t_wdog_ms = 500,
    };

    app_t app;
    memset(&app, 0, sizeof app);
    app.flow_ml_per_tick = flow_ml;
    app.drop_at_ml = drop_at;
    ofp_pump_init(&app.pump, &cfg, hal_millis());
    hal_timer_register(tick_ms, flow_tick, &app);

    int listener = make_listener((uint16_t)port);
    if (listener < 0)
    {
        fprintf(stderr, "[pump %u] cannot listen: %s\n", port, strerror(errno));
        return 1;
    }
    fprintf(stderr, "[pump %u] listening (price=%u/L pulses/L=%u drop-at=%u manual=%u)\n",
            port, price, ppl, drop_at, manual);

    while (!g_stop)
    {
        int client = accept(listener, NULL, NULL);
        if (client < 0)
        {
            if (g_stop)
            {
                break;
            }
            continue;
        }
        int flag = 1;
        setsockopt(client, IPPROTO_TCP, TCP_NODELAY, &flag, sizeof flag);
        fcntl(client, F_SETFL, O_NONBLOCK);
        hal_host_set_uart_fd(client);
        ofp_frame_decoder_init(&app.pump.dec); /* discard any partial frame from a prior link */
        app.dropped = 0;
        fprintf(stderr, "[pump %u] manager connected\n", port);

        uint8_t prev_state = app.pump.state;
        uint32_t auth_at = 0;
        int lifted = 0;

        while (!g_stop)
        {
            int rx = ofp_pump_poll_rx(&app.pump);
            if (rx < 0)
            {
                break; /* manager disconnected — go back to accept (§8 reconnect) */
            }
            uint32_t now = hal_millis();
            ofp_pump_kick_watchdog(&app.pump, now);
            hal_host_service_timers(now);

            /* Customer script: lift the nozzle a moment after authorisation. */
            if (!manual)
            {
                if (prev_state != OFP_STATE_AUTHORISED && app.pump.state == OFP_STATE_AUTHORISED)
                {
                    auth_at = now;
                    lifted = 0;
                    app.dropped = 0;
                }
                if (app.pump.state == OFP_STATE_AUTHORISED && !lifted &&
                    (now - auth_at) >= nozzle_delay)
                {
                    lifted = 1;
                    ofp_pump_nozzle_up(&app.pump);
                }
                if (app.pump.state == OFP_STATE_IDLE)
                {
                    lifted = 0;
                }
            }
            prev_state = app.pump.state;

            ofp_pump_tick(&app.pump, now);

            struct timespec nap = {0, 2 * 1000 * 1000}; /* 2 ms; host pacing, not core logic */
            nanosleep(&nap, NULL);
        }
        hal_host_set_uart_fd(-1);
        close(client);
        fprintf(stderr, "[pump %u] manager disconnected\n", port);
    }

    close(listener);
    fprintf(stderr, "[pump %u] shutdown\n", port);
    return 0;
}
