#include "fsm.h"

#include "protocol.h"

/* Compact cell constructors for the table literal below. */
#define ILL {OFP_FSM_ILLEGAL, 0, 0}
#define IGN {OFP_FSM_IGNORED, 0, 0}
#define GO(s, a) {OFP_FSM_LEGAL, (s), (a)}

/* The transition table, rows = states (§6 codes), columns = ofp_fsm_event order.
 * This literal IS the state machine; it mirrors docs/protocol-pump.md §6.1 one-for-one. */
static const ofp_fsm_result TABLE[6][OFP_FSM_EVENT_COUNT] = {
    /* AUTHORISE                                 CANCEL                             SUSPEND  RESUME  NOZZLE_UP                          NOZZLE_DOWN                        FLOW                               LIMIT                              FAULT */
    /* IDLE       */ {GO(OFP_STATE_AUTHORISED, OFP_ACT_LOAD_PRESET), GO(OFP_STATE_IDLE, 0), ILL, ILL, GO(OFP_STATE_FAULT, OFP_ACT_RAISE_FAULT), ILL, GO(OFP_STATE_FAULT, OFP_ACT_RAISE_FAULT), ILL, GO(OFP_STATE_FAULT, OFP_ACT_RAISE_FAULT)},
    /* AUTHORISED */ {ILL, GO(OFP_STATE_IDLE, OFP_ACT_RESET_DISPENSE), ILL, ILL, GO(OFP_STATE_DISPENSING, OFP_ACT_START_DISPENSE), ILL, ILL, ILL, GO(OFP_STATE_FAULT, OFP_ACT_RAISE_FAULT)},
    /* DISPENSING */ {ILL, ILL, GO(OFP_STATE_SUSPENDED, OFP_ACT_HALT_FLOW), ILL, ILL, GO(OFP_STATE_COMPLETE, OFP_ACT_EMIT_COMPLETE), GO(OFP_STATE_DISPENSING, OFP_ACT_ACCUM_FLOW), GO(OFP_STATE_COMPLETE, OFP_ACT_EMIT_COMPLETE), GO(OFP_STATE_FAULT, OFP_ACT_RAISE_FAULT)},
    /* COMPLETE   */ {GO(OFP_STATE_AUTHORISED, OFP_ACT_LOAD_PRESET), GO(OFP_STATE_IDLE, OFP_ACT_RESET_DISPENSE), ILL, ILL, ILL, ILL, ILL, ILL, GO(OFP_STATE_FAULT, OFP_ACT_RAISE_FAULT)},
    /* SUSPENDED  */ {ILL, GO(OFP_STATE_IDLE, OFP_ACT_RESET_DISPENSE), ILL, GO(OFP_STATE_DISPENSING, 0), ILL, GO(OFP_STATE_COMPLETE, OFP_ACT_EMIT_COMPLETE), IGN, ILL, GO(OFP_STATE_FAULT, OFP_ACT_RAISE_FAULT)},
    /* FAULT      */ {ILL, GO(OFP_STATE_IDLE, OFP_ACT_CLEAR_FAULT), ILL, ILL, ILL, ILL, IGN, ILL, IGN},
};

ofp_fsm_result ofp_fsm_next(uint8_t state, ofp_fsm_event ev)
{
    if (state > OFP_STATE_FAULT || (int)ev < 0 || (int)ev >= OFP_FSM_EVENT_COUNT)
    {
        ofp_fsm_result bad = ILL;
        return bad;
    }
    return TABLE[state][ev];
}
