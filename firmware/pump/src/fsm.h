/* Dispenser finite state machine — the transition table of docs/protocol-pump.md §6.1,
 * as an explicit data table (CLAUDE.md §6: "All state machines are explicit tables ...").
 * Pure: no I/O, no HAL, no global state, so every cell is unit-testable directly. The
 * pump module (pump.c) wraps this and performs the I/O the actions describe. */
#ifndef OFP_FSM_H
#define OFP_FSM_H

#include <stdint.h>

/* Events that drive the FSM. Some come from received frames (AUTHORISE, CANCEL, SUSPEND,
 * RESUME, NOZZLE_UP, NOZZLE_DOWN); some are internal (FLOW pulse batch, preset LIMIT
 * reached, FAULT detector). STATUS_REQ/TOTALS_REQ are pure reads and never enter here. */
typedef enum
{
    OFP_FSM_EV_AUTHORISE = 0,
    OFP_FSM_EV_CANCEL = 1,
    OFP_FSM_EV_SUSPEND = 2,
    OFP_FSM_EV_RESUME = 3,
    OFP_FSM_EV_NOZZLE_UP = 4,
    OFP_FSM_EV_NOZZLE_DOWN = 5,
    OFP_FSM_EV_FLOW = 6,
    OFP_FSM_EV_LIMIT = 7,
    OFP_FSM_EV_FAULT = 8,
    OFP_FSM_EVENT_COUNT = 9
} ofp_fsm_event;

/* Outcome of applying an event in a state. */
typedef enum
{
    OFP_FSM_ILLEGAL = 0, /* must not occur; command -> NAK ERR_ILLEGAL_STATE, no change */
    OFP_FSM_LEGAL = 1, /* transitions to next_state, run actions */
    OFP_FSM_IGNORED = 2 /* accepted, no state change, no side effect */
} ofp_fsm_outcome;

/* Action flags a legal transition asks the wrapper to perform (see §6.1 side effects). */
#define OFP_ACT_LOAD_PRESET 0x01u /* latch preset + reset per-dispense counters */
#define OFP_ACT_START_DISPENSE 0x02u /* nozzle lifted, begin flow */
#define OFP_ACT_EMIT_COMPLETE 0x04u /* seal totalizer, emit DISPENSE_COMPLETE */
#define OFP_ACT_ACCUM_FLOW 0x08u /* accumulate pulses, emit FLOW_UPDATE */
#define OFP_ACT_HALT_FLOW 0x10u /* pause dispensing */
#define OFP_ACT_RESET_DISPENSE 0x20u /* clear per-dispense counters */
#define OFP_ACT_CLEAR_FAULT 0x40u /* leave FAULT, clear latched code */
#define OFP_ACT_RAISE_FAULT 0x80u /* enter FAULT, latch code (wrapper supplies code) */

typedef struct
{
    ofp_fsm_outcome outcome;
    uint8_t next_state; /* meaningful only when outcome == OFP_FSM_LEGAL */
    uint8_t actions; /* OFP_ACT_* bitmask */
} ofp_fsm_result;

/* Look up one cell of the transition table. Out-of-range inputs are ILLEGAL. */
ofp_fsm_result ofp_fsm_next(uint8_t state, ofp_fsm_event ev);

#endif /* OFP_FSM_H */
