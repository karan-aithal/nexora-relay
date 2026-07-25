/* Exhaustive FSM test: every (state, event) cell of docs/protocol-pump.md §6.1, checked
 * against an INDEPENDENT expected table restated here from the document. If the production
 * table (fsm.c) and this restatement disagree, one of them is wrong — that is the point. */
#include "../src/fsm.h"
#include "../src/protocol.h"
#include "ofp_test.h"

/* Expected outcome + next state for each cell; next_state ignored unless LEGAL. */
typedef struct
{
    ofp_fsm_outcome outcome;
    uint8_t next;
} expect;

#define L(s) {OFP_FSM_LEGAL, (s)}
#define ILL {OFP_FSM_ILLEGAL, 0}
#define IGN {OFP_FSM_IGNORED, 0}

/* Rows: IDLE, AUTHORISED, DISPENSING, COMPLETE, SUSPENDED, FAULT.
 * Cols: AUTHORISE, CANCEL, SUSPEND, RESUME, NOZZLE_UP, NOZZLE_DOWN, FLOW, LIMIT, FAULT. */
static const expect EXPECT[6][OFP_FSM_EVENT_COUNT] = {
    /* IDLE */ {L(OFP_STATE_AUTHORISED), L(OFP_STATE_IDLE), ILL, ILL, L(OFP_STATE_FAULT), ILL, L(OFP_STATE_FAULT), ILL, L(OFP_STATE_FAULT)},
    /* AUTHORISED */ {ILL, L(OFP_STATE_IDLE), ILL, ILL, L(OFP_STATE_DISPENSING), ILL, ILL, ILL, L(OFP_STATE_FAULT)},
    /* DISPENSING */ {ILL, ILL, L(OFP_STATE_SUSPENDED), ILL, ILL, L(OFP_STATE_COMPLETE), L(OFP_STATE_DISPENSING), L(OFP_STATE_COMPLETE), L(OFP_STATE_FAULT)},
    /* COMPLETE */ {L(OFP_STATE_AUTHORISED), L(OFP_STATE_IDLE), ILL, ILL, ILL, ILL, ILL, ILL, L(OFP_STATE_FAULT)},
    /* SUSPENDED */ {ILL, L(OFP_STATE_IDLE), ILL, L(OFP_STATE_DISPENSING), ILL, L(OFP_STATE_COMPLETE), IGN, ILL, L(OFP_STATE_FAULT)},
    /* FAULT */ {ILL, L(OFP_STATE_IDLE), ILL, ILL, ILL, ILL, IGN, ILL, IGN},
};

int main(void)
{
    for (uint8_t s = 0; s <= OFP_STATE_FAULT; s++)
    {
        for (int e = 0; e < OFP_FSM_EVENT_COUNT; e++)
        {
            ofp_fsm_result r = ofp_fsm_next(s, (ofp_fsm_event)e);
            expect x = EXPECT[s][e];
            CHECK_EQ_U(r.outcome, x.outcome);
            if (x.outcome == OFP_FSM_LEGAL)
            {
                CHECK_EQ_U(r.next_state, x.next);
            }
        }
    }

    /* Spot-check the side-effect flags that carry money/safety meaning. */
    CHECK(ofp_fsm_next(OFP_STATE_IDLE, OFP_FSM_EV_AUTHORISE).actions & OFP_ACT_LOAD_PRESET);
    CHECK(ofp_fsm_next(OFP_STATE_DISPENSING, OFP_FSM_EV_LIMIT).actions & OFP_ACT_EMIT_COMPLETE);
    CHECK(ofp_fsm_next(OFP_STATE_DISPENSING, OFP_FSM_EV_NOZZLE_DOWN).actions & OFP_ACT_EMIT_COMPLETE);
    CHECK(ofp_fsm_next(OFP_STATE_FAULT, OFP_FSM_EV_CANCEL).actions & OFP_ACT_CLEAR_FAULT);

    /* Out-of-range inputs are ILLEGAL, never a table over-read. */
    CHECK_EQ_U(ofp_fsm_next(99, OFP_FSM_EV_CANCEL).outcome, OFP_FSM_ILLEGAL);

    TEST_SUMMARY("test_fsm");
}
