#include "../src/crc16.h"
#include "ofp_test.h"

int main(void)
{
    /* The pinned check value from docs/protocol-pump.md §1.1 — this is what makes
     * "CRC16-CCITT" unambiguous. If this passes, the algorithm is the one we mean. */
    const uint8_t check[] = {'1', '2', '3', '4', '5', '6', '7', '8', '9'};
    CHECK_EQ_U(ofp_crc16(check, sizeof check), 0x29B1u);

    /* Empty input is the raw init value (no bytes clocked in). */
    CHECK_EQ_U(ofp_crc16(NULL, 0), 0xFFFFu);

    /* The AUTHORISE body from the worked example §9.1: 07 01 02 00 00 03 E8 -> 0xC0DC. */
    const uint8_t body[] = {0x07, 0x01, 0x02, 0x00, 0x00, 0x03, 0xE8};
    CHECK_EQ_U(ofp_crc16(body, sizeof body), 0xC0DCu);

    TEST_SUMMARY("test_crc");
}
