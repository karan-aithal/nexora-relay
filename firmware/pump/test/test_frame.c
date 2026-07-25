/* Frame codec tests: the golden AUTHORISE frame (byte-for-byte against docs §9.1), a
 * round-trip through the streaming decoder, byte-stuffing edge cases, CRC rejection, and
 * mid-stream resynchronisation. The golden frame is the cross-language anchor: the C#
 * codec test asserts the identical bytes. */
#include "../src/frame.h"
#include "../src/protocol.h"
#include "ofp_test.h"

/* Feed a whole buffer to the decoder, returning the status of the last byte. */
static ofp_frame_status feed_all(ofp_frame_decoder *d, const uint8_t *buf, size_t len)
{
    ofp_frame_status st = OFP_FRAME_NONE;
    for (size_t i = 0; i < len; i++)
    {
        st = ofp_frame_feed(d, buf[i]);
    }
    return st;
}

int main(void)
{
    uint8_t out[OFP_MAX_FRAME];

    /* ---- golden AUTHORISE frame (§9.1): SEQ 07, CMD 01, payload 02 000003E8 ----
     * The logical body 00 07 07 01 02 00 00 03 E8 C0 DC contains 0x02 (preset mode) and
     * 0x03 (in the amount), so both are byte-stuffed on the wire: this golden frame IS the
     * stuffing demonstration, and the C# codec test asserts these identical 15 bytes. */
    const uint8_t auth_payload[] = {0x02, 0x00, 0x00, 0x03, 0xE8};
    int n = ofp_frame_encode(0x07, OFP_CMD_AUTHORISE, auth_payload, sizeof auth_payload, out,
                             sizeof out);
    const uint8_t golden[] = {0x02, 0x00, 0x07, 0x07, 0x01, 0x10, 0x22, 0x00,
                              0x00, 0x10, 0x23, 0xE8, 0xC0, 0xDC, 0x03};
    CHECK_EQ_U(n, (int)sizeof golden);
    if (n == (int)sizeof golden)
    {
        for (size_t i = 0; i < sizeof golden; i++)
        {
            CHECK_EQ_U(out[i], golden[i]);
        }
    }

    /* ---- round-trip through the decoder ---- */
    ofp_frame_decoder d;
    ofp_frame_decoder_init(&d);
    CHECK_EQ_U(feed_all(&d, out, (size_t)n), OFP_FRAME_READY);
    CHECK_EQ_U(d.seq, 0x07u);
    CHECK_EQ_U(d.cmd, OFP_CMD_AUTHORISE);
    CHECK_EQ_U(d.payload_len, sizeof auth_payload);
    for (size_t i = 0; i < sizeof auth_payload; i++)
    {
        CHECK_EQ_U(d.payload[i], auth_payload[i]);
    }

    /* ---- byte stuffing: SEQ and payload contain reserved bytes 02/03/10 ---- */
    const uint8_t stuff_payload[] = {0x02, 0x03, 0x10, 0x20};
    n = ofp_frame_encode(0x10 /* SEQ needs stuffing too */, 0x22, stuff_payload,
                         sizeof stuff_payload, out, sizeof out);
    CHECK(n > 0);
    /* Between STX and ETX there must be no raw 02/03, and 10 must only appear as an escape
     * prefix (followed by a XOR-0x20 byte). */
    for (int i = 1; i < n - 1; i++)
    {
        CHECK(out[i] != OFP_STX);
        CHECK(out[i] != OFP_ETX);
    }
    ofp_frame_decoder_init(&d);
    CHECK_EQ_U(feed_all(&d, out, (size_t)n), OFP_FRAME_READY);
    CHECK_EQ_U(d.seq, 0x10u);
    CHECK_EQ_U(d.cmd, 0x22u);
    CHECK_EQ_U(d.payload_len, sizeof stuff_payload);
    for (size_t i = 0; i < sizeof stuff_payload; i++)
    {
        CHECK_EQ_U(d.payload[i], stuff_payload[i]);
    }

    /* ---- CRC rejection: corrupt a payload byte after encoding ----
     * Chosen so nothing needs stuffing: LEN=0x0005 (payload 3), SEQ/CMD/payload all
     * non-reserved, so payload byte 0 sits at a fixed wire offset 5. */
    const uint8_t plain[] = {0xAA, 0xBB, 0xCC};
    n = ofp_frame_encode(0x40, 0x22, plain, sizeof plain, out, sizeof out);
    CHECK(n > 6);
    out[5] ^= 0xFFu; /* corrupt payload[0] on the wire; 0xAA^0xFF=0x55, still non-reserved */
    ofp_frame_decoder_init(&d);
    CHECK_EQ_U(feed_all(&d, out, (size_t)n), OFP_FRAME_ERR);
    CHECK_EQ_U(d.last_error, OFP_ERR_BAD_CRC);

    /* ---- resync: junk + a truncated frame prefix, then a clean frame ---- */
    ofp_frame_decoder_init(&d);
    const uint8_t junk[] = {0xAA, 0xBB, OFP_STX, 0x99}; /* stray STX starts a doomed frame */
    feed_all(&d, junk, sizeof junk);
    n = ofp_frame_encode(0x2A, OFP_CMD_TOTALS, NULL, 0, out, sizeof out);
    CHECK_EQ_U(feed_all(&d, out, (size_t)n), OFP_FRAME_READY);
    CHECK_EQ_U(d.seq, 0x2Au);
    CHECK_EQ_U(d.cmd, OFP_CMD_TOTALS);

    /* ---- oversize guard: encoding a payload beyond OFP_MAX_PAYLOAD is refused ---- */
    uint8_t big[OFP_MAX_PAYLOAD + 1];
    for (size_t i = 0; i < sizeof big; i++)
    {
        big[i] = 0x41;
    }
    CHECK_EQ_U(ofp_frame_encode(1, 1, big, sizeof big, out, sizeof out), -1);

    TEST_SUMMARY("test_frame");
}
