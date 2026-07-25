#include "pump.h"

#include <string.h>

#include "fsm.h"
#include "hal.h"
#include "protocol.h"

/* ---- small byte helpers (all wire integers are big-endian, §1) ---- */

static void put_be32(uint8_t *b, uint32_t v)
{
    b[0] = (uint8_t)(v >> 24);
    b[1] = (uint8_t)(v >> 16);
    b[2] = (uint8_t)(v >> 8);
    b[3] = (uint8_t)v;
}

static void put_be64(uint8_t *b, uint64_t v)
{
    put_be32(b, (uint32_t)(v >> 32));
    put_be32(b + 4, (uint32_t)v);
}

static uint32_t get_be32(const uint8_t *b)
{
    return ((uint32_t)b[0] << 24) | ((uint32_t)b[1] << 16) | ((uint32_t)b[2] << 8) | b[3];
}

/* ---- outputs reflect state; one place, so no action needs to touch GPIO directly ---- */

static void apply_outputs(const ofp_pump *p)
{
    hal_gpio_set(OFP_GPIO_VALVE, p->state == OFP_STATE_DISPENSING ? 1 : 0);
    hal_gpio_set(OFP_GPIO_FAULT_LAMP, p->state == OFP_STATE_FAULT ? 1 : 0);
}

/* ---- frame emission ---- */

/* Send a command response and cache it for idempotent retransmit (§4.3). */
static void respond(ofp_pump *p, uint8_t seq, uint8_t cmd, const uint8_t *payload, size_t len)
{
    uint8_t buf[OFP_MAX_FRAME];
    int n = ofp_frame_encode(seq, cmd, payload, len, buf, sizeof buf);
    if (n <= 0)
    {
        return;
    }
    hal_uart_write(buf, (size_t)n);
    memcpy(p->last_resp, buf, (size_t)n);
    p->last_resp_len = (size_t)n;
    p->have_last_cmd = 1;
    p->last_cmd_seq = seq;
}

static void nak(ofp_pump *p, uint8_t seq, uint8_t err)
{
    uint8_t code = err;
    respond(p, seq, OFP_NAK, &code, 1);
}

/* Emit an unsolicited event with a specific event SEQ (used for DISPENSE_COMPLETE re-emit,
 * which must reuse the same SEQ, §4.4). Does not touch the idempotency cache. */
static void emit_event_seq(uint8_t seq, uint8_t cmd, const uint8_t *payload, size_t len)
{
    uint8_t buf[OFP_MAX_FRAME];
    int n = ofp_frame_encode(seq, cmd, payload, len, buf, sizeof buf);
    if (n > 0)
    {
        hal_uart_write(buf, (size_t)n);
    }
}

/* Emit an unsolicited event with the next pump event SEQ. */
static void emit_event(ofp_pump *p, uint8_t cmd, const uint8_t *payload, size_t len)
{
    emit_event_seq(p->event_seq, cmd, payload, len);
    p->event_seq++;
}

/* ---- fault handling ---- */

static void raise_fault(ofp_pump *p, uint8_t code)
{
    p->state = OFP_STATE_FAULT;
    p->fault_code = code;
    apply_outputs(p);
    uint8_t payload = code;
    emit_event(p, OFP_EVT_FAULT, &payload, 1);
}

/* ---- dispense completion: seal the totalizer, emit DISPENSE_COMPLETE ---- */

static void do_complete(ofp_pump *p)
{
    /* Totalizer sealed at delivery end, not settle-ack (§7): never lost or double-counted. */
    p->lifetime_ml += p->dispense_ml;
    p->lifetime_value += p->dispense_value;
    apply_outputs(p);

    uint8_t payload[8];
    put_be32(payload, p->dispense_ml);
    put_be32(payload + 4, p->dispense_value);
    p->complete_event_seq = p->event_seq++;
    emit_event_seq(p->complete_event_seq, OFP_EVT_DISPENSE_COMPLETE, payload, sizeof payload);
    p->complete_last_emit_ms = hal_millis();
}

/* ---- init ---- */

void ofp_pump_init(ofp_pump *p, const ofp_pump_config *cfg, uint32_t now_ms)
{
    memset(p, 0, sizeof *p);
    p->cfg = *cfg;
    p->state = OFP_STATE_IDLE;
    p->fault_code = OFP_FAULT_NONE;
    p->have_last_cmd = 0;
    p->last_wdog_kick_ms = now_ms;
    ofp_frame_decoder_init(&p->dec);
    apply_outputs(p);
}

/* ---- inbound command handling ---- */

static void handle_authorise(ofp_pump *p, uint8_t seq, const uint8_t *payload, size_t len)
{
    if (len < 5 || (payload[0] != OFP_PRESET_NONE && payload[0] != OFP_PRESET_VOLUME &&
                    payload[0] != OFP_PRESET_VALUE))
    {
        nak(p, seq, OFP_ERR_PRESET_INVALID);
        return;
    }
    ofp_fsm_result r = ofp_fsm_next(p->state, OFP_FSM_EV_AUTHORISE);
    if (r.outcome != OFP_FSM_LEGAL)
    {
        nak(p, seq, OFP_ERR_ILLEGAL_STATE);
        return;
    }
    /* LOAD_PRESET: latch preset and reset the per-dispense counters. */
    p->preset_mode = payload[0];
    p->preset_limit = get_be32(payload + 1);
    p->dispense_ml = 0;
    p->dispense_value = 0;
    p->state = r.next_state;
    apply_outputs(p);
    respond(p, seq, (uint8_t)(OFP_CMD_AUTHORISE | OFP_ACK_MASK), NULL, 0);
}

/* CANCEL / SUSPEND / RESUME: pure FSM commands with no payload. */
static void handle_simple(ofp_pump *p, uint8_t seq, uint8_t cmd, ofp_fsm_event ev)
{
    ofp_fsm_result r = ofp_fsm_next(p->state, ev);
    if (r.outcome != OFP_FSM_LEGAL)
    {
        nak(p, seq, OFP_ERR_ILLEGAL_STATE);
        return;
    }
    if (r.actions & OFP_ACT_RESET_DISPENSE)
    {
        p->dispense_ml = 0;
        p->dispense_value = 0;
    }
    if (r.actions & OFP_ACT_CLEAR_FAULT)
    {
        p->fault_code = OFP_FAULT_NONE;
    }
    p->state = r.next_state;
    apply_outputs(p);
    respond(p, seq, (uint8_t)(cmd | OFP_ACK_MASK), NULL, 0);
}

static void handle_status(ofp_pump *p, uint8_t seq)
{
    uint8_t payload[9];
    payload[0] = p->state;
    put_be32(payload + 1, p->dispense_ml);
    put_be32(payload + 5, p->dispense_value);
    respond(p, seq, (uint8_t)(OFP_CMD_STATUS | OFP_ACK_MASK), payload, sizeof payload);
}

static void handle_totals(ofp_pump *p, uint8_t seq)
{
    uint8_t payload[16];
    put_be64(payload, p->lifetime_ml);
    put_be64(payload + 8, p->lifetime_value);
    respond(p, seq, (uint8_t)(OFP_CMD_TOTALS | OFP_ACK_MASK), payload, sizeof payload);
}

static void handle_frame(ofp_pump *p, uint8_t seq, uint8_t cmd, const uint8_t *payload, size_t len)
{
    /* Idempotent duplicate: same SEQ as last processed command -> resend cached response,
     * do NOT re-execute the side effect (§4.3). Critical for AUTHORISE. */
    if (p->have_last_cmd && seq == p->last_cmd_seq)
    {
        hal_uart_write(p->last_resp, p->last_resp_len);
        return;
    }

    switch (cmd)
    {
    case OFP_CMD_AUTHORISE:
        handle_authorise(p, seq, payload, len);
        break;
    case OFP_CMD_CANCEL:
        handle_simple(p, seq, OFP_CMD_CANCEL, OFP_FSM_EV_CANCEL);
        break;
    case OFP_CMD_SUSPEND:
        handle_simple(p, seq, OFP_CMD_SUSPEND, OFP_FSM_EV_SUSPEND);
        break;
    case OFP_CMD_RESUME:
        handle_simple(p, seq, OFP_CMD_RESUME, OFP_FSM_EV_RESUME);
        break;
    case OFP_CMD_STATUS:
        handle_status(p, seq);
        break;
    case OFP_CMD_TOTALS:
        handle_totals(p, seq);
        break;
    default:
        nak(p, seq, OFP_ERR_UNKNOWN_CMD);
        break;
    }
}

int ofp_pump_poll_rx(ofp_pump *p)
{
    uint8_t buf[64];
    int n = hal_uart_read(buf, sizeof buf);
    if (n < 0)
    {
        return -1; /* link closed */
    }
    if (n == 0)
    {
        return 0; /* nothing available — non-blocking */
    }
    for (int i = 0; i < n; i++)
    {
        ofp_frame_status st = ofp_frame_feed(&p->dec, buf[i]);
        if (st == OFP_FRAME_READY)
        {
            handle_frame(p, p->dec.seq, p->dec.cmd, p->dec.payload, p->dec.payload_len);
        }
        /* OFP_FRAME_ERR: corrupt frame dropped; manager recovers by timeout+retry (§5 note). */
    }
    return 1;
}

/* ---- physical-world inputs ---- */

void ofp_pump_nozzle_up(ofp_pump *p)
{
    ofp_fsm_result r = ofp_fsm_next(p->state, OFP_FSM_EV_NOZZLE_UP);
    if (r.outcome != OFP_FSM_LEGAL)
    {
        return; /* spurious nozzle event in a state that ignores it */
    }
    if (r.actions & OFP_ACT_RAISE_FAULT)
    {
        raise_fault(p, OFP_FAULT_NOZZLE_UNEXPECTED); /* lifted while IDLE */
        return;
    }
    p->state = r.next_state; /* -> DISPENSING */
    p->last_pulse_ms = hal_millis();
    apply_outputs(p);
    emit_event(p, OFP_EVT_NOZZLE_UP, NULL, 0);
}

void ofp_pump_nozzle_down(ofp_pump *p)
{
    ofp_fsm_result r = ofp_fsm_next(p->state, OFP_FSM_EV_NOZZLE_DOWN);
    if (r.outcome != OFP_FSM_LEGAL)
    {
        return;
    }
    emit_event(p, OFP_EVT_NOZZLE_DOWN, NULL, 0);
    p->state = r.next_state; /* -> COMPLETE */
    if (r.actions & OFP_ACT_EMIT_COMPLETE)
    {
        do_complete(p);
    }
    apply_outputs(p);
}

/* Recompute the money value of the current dispense from its volume (§7). */
static void recompute_value(ofp_pump *p)
{
    p->dispense_value = (uint32_t)((uint64_t)p->dispense_ml * p->cfg.unit_price_per_litre / 1000u);
}

void ofp_pump_on_pulses(ofp_pump *p, uint32_t pulses)
{
    ofp_fsm_result r = ofp_fsm_next(p->state, OFP_FSM_EV_FLOW);
    if (r.outcome == OFP_FSM_IGNORED)
    {
        return; /* suspended/faulted: stray pulses swallowed (§6.1) */
    }
    if (r.outcome != OFP_FSM_LEGAL)
    {
        return; /* illegal cell: drop */
    }
    if (r.actions & OFP_ACT_RAISE_FAULT)
    {
        raise_fault(p, OFP_FAULT_FLOW_WHILE_IDLE); /* flow while IDLE */
        return;
    }
    /* ACCUM_FLOW: convert pulses to mL, accumulate, recompute value, honour preset limit. */
    uint32_t delta_ml = (uint32_t)((uint64_t)pulses * 1000u / p->cfg.pulses_per_litre);
    p->dispense_ml += delta_ml;
    recompute_value(p);

    int limit_reached = 0;
    if (p->preset_mode == OFP_PRESET_VOLUME && p->dispense_ml >= p->preset_limit)
    {
        p->dispense_ml = p->preset_limit; /* clamp to exactly the preset */
        recompute_value(p);
        limit_reached = 1;
    }
    else if (p->preset_mode == OFP_PRESET_VALUE && p->dispense_value >= p->preset_limit)
    {
        p->dispense_value = p->preset_limit;
        p->dispense_ml = (uint32_t)((uint64_t)p->preset_limit * 1000u / p->cfg.unit_price_per_litre);
        limit_reached = 1;
    }
    p->last_pulse_ms = hal_millis();

    uint8_t payload[8];
    put_be32(payload, p->dispense_ml);
    put_be32(payload + 4, p->dispense_value);
    emit_event(p, OFP_EVT_FLOW_UPDATE, payload, sizeof payload);

    if (limit_reached)
    {
        ofp_fsm_result lr = ofp_fsm_next(p->state, OFP_FSM_EV_LIMIT);
        if (lr.outcome == OFP_FSM_LEGAL)
        {
            p->state = lr.next_state; /* -> COMPLETE */
            do_complete(p);
        }
    }
}

/* ---- housekeeping ---- */

void ofp_pump_kick_watchdog(ofp_pump *p, uint32_t now_ms)
{
    p->last_wdog_kick_ms = now_ms;
}

void ofp_pump_tick(ofp_pump *p, uint32_t now_ms)
{
    if (p->state != OFP_STATE_FAULT && (now_ms - p->last_wdog_kick_ms) > p->cfg.t_wdog_ms)
    {
        raise_fault(p, OFP_FAULT_WATCHDOG);
        return;
    }
    if (p->state == OFP_STATE_DISPENSING && (now_ms - p->last_pulse_ms) > p->cfg.t_stall_ms)
    {
        raise_fault(p, OFP_FAULT_METER_STALL);
        return;
    }
    /* Re-emit the critical DISPENSE_COMPLETE with its fixed SEQ until acked (§4.4). */
    if (p->state == OFP_STATE_COMPLETE &&
        (now_ms - p->complete_last_emit_ms) >= p->cfg.t_event_repeat_ms)
    {
        uint8_t payload[8];
        put_be32(payload, p->dispense_ml);
        put_be32(payload + 4, p->dispense_value);
        emit_event_seq(p->complete_event_seq, OFP_EVT_DISPENSE_COMPLETE, payload, sizeof payload);
        p->complete_last_emit_ms = now_ms;
    }
}
