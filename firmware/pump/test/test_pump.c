/* Pump integration test over the fake HAL: a full value-preset fuelling end to end, the
 * idempotent-duplicate rule on AUTHORISE (the money-critical case), preset clamping, the
 * sealed totalizer, and NAK paths. Exercises pump.c's wiring of FSM + framing + totals. */
#include <string.h>

#include "../hal/hal.h"
#include "../src/frame.h"
#include "../src/protocol.h"
#include "../src/pump.h"
#include "fake_hal.h"
#include "ofp_test.h"

typedef struct
{
    uint8_t seq;
    uint8_t cmd;
    uint8_t payload[OFP_MAX_PAYLOAD];
    size_t len;
} capframe;

static int scan_tx(capframe *frames, int cap)
{
    size_t txlen;
    const uint8_t *tx = fake_hal_tx(&txlen);
    ofp_frame_decoder d;
    ofp_frame_decoder_init(&d);
    int cnt = 0;
    for (size_t i = 0; i < txlen; i++)
    {
        if (ofp_frame_feed(&d, tx[i]) == OFP_FRAME_READY && cnt < cap)
        {
            frames[cnt].seq = d.seq;
            frames[cnt].cmd = d.cmd;
            frames[cnt].len = d.payload_len;
            memcpy(frames[cnt].payload, d.payload, d.payload_len);
            cnt++;
        }
    }
    return cnt;
}

static int find_last(const capframe *fr, int cnt, uint8_t cmd, capframe *out)
{
    for (int i = cnt - 1; i >= 0; i--)
    {
        if (fr[i].cmd == cmd)
        {
            *out = fr[i];
            return 1;
        }
    }
    return 0;
}

static void send_cmd(uint8_t seq, uint8_t cmd, const uint8_t *pl, size_t len)
{
    uint8_t buf[OFP_MAX_FRAME];
    int n = ofp_frame_encode(seq, cmd, pl, len, buf, sizeof buf);
    fake_hal_push_rx(buf, (size_t)n);
}

static uint32_t be32(const uint8_t *b)
{
    return ((uint32_t)b[0] << 24) | ((uint32_t)b[1] << 16) | ((uint32_t)b[2] << 8) | b[3];
}

int main(void)
{
    capframe fr[64];
    capframe f;
    fake_hal_reset();

    ofp_pump_config cfg = {
        .unit_price_per_litre = 150, /* £1.50/L */
        .pulses_per_litre = 1000,
        .t_event_repeat_ms = 1000,
        .t_stall_ms = 3000,
        .t_wdog_ms = 500,
    };
    ofp_pump p;
    ofp_pump_init(&p, &cfg, 0);

    /* ---- AUTHORISE £10.00 value preset, SEQ 7 ---- */
    const uint8_t ap[5] = {OFP_PRESET_VALUE, 0x00, 0x00, 0x03, 0xE8}; /* 1000 minor units */
    send_cmd(7, OFP_CMD_AUTHORISE, ap, sizeof ap);
    ofp_pump_poll_rx(&p);
    CHECK_EQ_U(p.state, OFP_STATE_AUTHORISED);
    CHECK(find_last(fr, scan_tx(fr, 64), OFP_CMD_AUTHORISE | OFP_ACK_MASK, &f));
    CHECK_EQ_U(f.seq, 7u);

    /* ---- duplicate AUTHORISE, SAME SEQ 7: idempotent re-ACK, no second authorisation ---- */
    fake_hal_clear_tx();
    send_cmd(7, OFP_CMD_AUTHORISE, ap, sizeof ap);
    ofp_pump_poll_rx(&p);
    CHECK_EQ_U(p.state, OFP_STATE_AUTHORISED); /* unchanged */
    CHECK(find_last(fr, scan_tx(fr, 64), OFP_CMD_AUTHORISE | OFP_ACK_MASK, &f)); /* cached ACK */
    CHECK_EQ_U(p.event_seq, 0u); /* no event emitted by a dup */

    /* ---- nozzle lift: NOZZLE_UP event, valve energised ---- */
    fake_hal_clear_tx();
    ofp_pump_nozzle_up(&p);
    CHECK_EQ_U(p.state, OFP_STATE_DISPENSING);
    CHECK_EQ_U(fake_hal_gpio(OFP_GPIO_VALVE), 1);
    CHECK(find_last(fr, scan_tx(fr, 64), OFP_EVT_NOZZLE_UP, &f));

    /* ---- flow past the value preset: clamps to 6666 mL / 1000 units, seals totalizer ---- */
    fake_hal_clear_tx();
    ofp_pump_on_pulses(&p, 7000); /* 7000 mL raw -> 1050 units, over the £10 preset */
    CHECK_EQ_U(p.state, OFP_STATE_COMPLETE);
    CHECK_EQ_U(p.dispense_ml, 6666u);
    CHECK_EQ_U(p.dispense_value, 1000u);
    CHECK_EQ_U(p.lifetime_ml, 6666u); /* sealed at completion, not settle-ack */
    CHECK_EQ_U(p.lifetime_value, 1000u);
    CHECK_EQ_U(fake_hal_gpio(OFP_GPIO_VALVE), 0);
    CHECK(find_last(fr, scan_tx(fr, 64), OFP_EVT_DISPENSE_COMPLETE, &f));
    CHECK_EQ_U(f.len, 8u);
    CHECK_EQ_U(be32(f.payload), 6666u);
    CHECK_EQ_U(be32(f.payload + 4), 1000u);

    /* ---- settle-ack CANCEL, SEQ 8: back to IDLE, dispense cleared, totalizer retained ---- */
    fake_hal_clear_tx();
    send_cmd(8, OFP_CMD_CANCEL, NULL, 0);
    ofp_pump_poll_rx(&p);
    CHECK_EQ_U(p.state, OFP_STATE_IDLE);
    CHECK_EQ_U(p.dispense_ml, 0u);
    CHECK_EQ_U(p.lifetime_ml, 6666u); /* monotonic, non-resettable */
    CHECK(find_last(fr, scan_tx(fr, 64), OFP_CMD_CANCEL | OFP_ACK_MASK, &f));
    CHECK_EQ_U(f.seq, 8u);

    /* ---- NAK paths: illegal-state command and unknown command ---- */
    fake_hal_clear_tx();
    send_cmd(9, OFP_CMD_SUSPEND, NULL, 0); /* SUSPEND in IDLE is illegal */
    ofp_pump_poll_rx(&p);
    CHECK(find_last(fr, scan_tx(fr, 64), OFP_NAK, &f));
    CHECK_EQ_U(f.seq, 9u);
    CHECK_EQ_U(f.payload[0], OFP_ERR_ILLEGAL_STATE);

    fake_hal_clear_tx();
    send_cmd(10, 0xEE, NULL, 0); /* unknown command code */
    ofp_pump_poll_rx(&p);
    CHECK(find_last(fr, scan_tx(fr, 64), OFP_NAK, &f));
    CHECK_EQ_U(f.payload[0], OFP_ERR_UNKNOWN_CMD);

    /* ---- volume preset clamps to exactly the litre limit ---- */
    fake_hal_reset();
    ofp_pump_init(&p, &cfg, 0);
    const uint8_t vp[5] = {OFP_PRESET_VOLUME, 0x00, 0x00, 0x13, 0x88}; /* 5000 mL */
    send_cmd(1, OFP_CMD_AUTHORISE, vp, sizeof vp);
    ofp_pump_poll_rx(&p);
    ofp_pump_nozzle_up(&p);
    ofp_pump_on_pulses(&p, 9000); /* overshoot */
    CHECK_EQ_U(p.state, OFP_STATE_COMPLETE);
    CHECK_EQ_U(p.dispense_ml, 5000u);
    CHECK_EQ_U(p.dispense_value, 750u); /* 5.000 L * 150 = 750 */

    TEST_SUMMARY("test_pump");
}
