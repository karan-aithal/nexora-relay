/* The dispenser controller: ties the FSM, totalizer, flow/preset logic and OFP-1 framing
 * into one portable unit. Uses only the HAL for I/O (hal.h). No dynamic allocation, no
 * printf, no blocking — the run loop just polls. See docs/protocol-pump.md. */
#ifndef OFP_PUMP_H
#define OFP_PUMP_H

#include <stdint.h>

#include "frame.h"

typedef struct
{
    uint32_t unit_price_per_litre; /* minor currency units per litre */
    uint32_t pulses_per_litre; /* flow-meter resolution (§7) */
    uint32_t t_event_repeat_ms; /* DISPENSE_COMPLETE re-emit period (§4.4) */
    uint32_t t_stall_ms; /* meter-stall fault threshold (§7) */
    uint32_t t_wdog_ms; /* watchdog threshold (§7) */
} ofp_pump_config;

typedef struct
{
    ofp_pump_config cfg;
    uint8_t state; /* OFP_STATE_* */
    uint8_t fault_code; /* OFP_FAULT_* while latched */

    uint8_t preset_mode; /* OFP_PRESET_* */
    uint32_t preset_limit; /* mL or minor units, per mode */
    uint32_t dispense_ml; /* current dispense */
    uint32_t dispense_value;

    uint64_t lifetime_ml; /* non-resettable totalizer (§7) */
    uint64_t lifetime_value;

    ofp_frame_decoder dec; /* inbound frame reassembly */

    int have_last_cmd; /* idempotency cache (§4.3) */
    uint8_t last_cmd_seq;
    uint8_t last_resp[OFP_MAX_FRAME];
    size_t last_resp_len;

    uint8_t event_seq; /* pump-owned event sequence (§4.1) */
    uint8_t complete_event_seq; /* fixed seq reused for DISPENSE_COMPLETE re-emit */

    uint32_t last_pulse_ms; /* stall detection */
    uint32_t last_wdog_kick_ms; /* watchdog */
    uint32_t complete_last_emit_ms; /* re-emit timer */
} ofp_pump;

/* Initialise a pump to IDLE with the given config. now_ms seeds the watchdog. */
void ofp_pump_init(ofp_pump *p, const ofp_pump_config *cfg, uint32_t now_ms);

/* Poll the link once: drain hal_uart_read and process any complete inbound frames.
 * Returns 1 if bytes were processed, 0 if idle, -1 if the link has closed. */
int ofp_pump_poll_rx(ofp_pump *p);

/* Physical-world inputs, driven by the platform's customer simulation (host) or sensors
 * (target). Each may emit unsolicited events and advance the FSM. */
void ofp_pump_nozzle_up(ofp_pump *p);
void ofp_pump_nozzle_down(ofp_pump *p);
void ofp_pump_on_pulses(ofp_pump *p, uint32_t pulses);

/* Periodic housekeeping: watchdog, meter-stall detection, critical-event re-emission. */
void ofp_pump_tick(ofp_pump *p, uint32_t now_ms);

/* Kick the software watchdog; call once per run-loop iteration. */
void ofp_pump_kick_watchdog(ofp_pump *p, uint32_t now_ms);

#endif /* OFP_PUMP_H */
