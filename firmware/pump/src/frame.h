/* OFP-1 frame codec: encode one frame, and a streaming byte-fed decoder.
 * Framing, byte-stuffing and CRC coverage per docs/protocol-pump.md §1. No dynamic
 * allocation — the decoder owns a fixed body buffer. */
#ifndef OFP_FRAME_H
#define OFP_FRAME_H

#include <stddef.h>
#include <stdint.h>

#include "protocol.h"

/* Encode SEQ/CMD/PAYLOAD into a complete wire frame (STX..ETX, stuffed, CRC appended).
 * Returns the encoded length, or -1 on bad argument / insufficient out capacity. */
int ofp_frame_encode(uint8_t seq, uint8_t cmd, const uint8_t *payload, size_t payload_len,
                     uint8_t *out, size_t out_cap);

/* Streaming decoder result. */
typedef enum
{
    OFP_FRAME_NONE = 0, /* need more bytes */
    OFP_FRAME_READY = 1, /* a valid frame is available in the decoder fields */
    OFP_FRAME_ERR = 2 /* a framing/CRC error was resolved (frame dropped) */
} ofp_frame_status;

typedef struct
{
    uint8_t body[OFP_MAX_BODY]; /* un-stuffed LEN|SEQ|CMD|PAYLOAD|CRC */
    size_t body_len;
    int in_frame; /* seen STX, not yet ETX */
    int esc; /* previous byte was ESC */
    int overflow; /* body exceeded OFP_MAX_BODY since last STX */

    /* Valid only when the last feed returned OFP_FRAME_READY: */
    uint8_t seq;
    uint8_t cmd;
    const uint8_t *payload;
    size_t payload_len;
    uint16_t last_error; /* OFP_ERR_* set when a feed returns OFP_FRAME_ERR */
} ofp_frame_decoder;

void ofp_frame_decoder_init(ofp_frame_decoder *d);

/* Feed one received byte. Returns OFP_FRAME_READY when a frame completes (fields set),
 * OFP_FRAME_ERR when a corrupt frame was dropped, OFP_FRAME_NONE otherwise. */
ofp_frame_status ofp_frame_feed(ofp_frame_decoder *d, uint8_t byte);

#endif /* OFP_FRAME_H */
