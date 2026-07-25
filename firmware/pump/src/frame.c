#include "frame.h"

#include "crc16.h"

/* Append one body byte, stuffing reserved bytes (STX/ETX/ESC). Returns bytes written or
 * -1 if it would overflow out_cap. */
static int stuff_into(uint8_t *out, size_t out_cap, size_t pos, uint8_t byte)
{
    if (byte == OFP_STX || byte == OFP_ETX || byte == OFP_ESC)
    {
        if (pos + 2 > out_cap)
        {
            return -1;
        }
        out[pos] = OFP_ESC;
        out[pos + 1] = (uint8_t)(byte ^ OFP_STUFF_XOR);
        return 2;
    }
    if (pos + 1 > out_cap)
    {
        return -1;
    }
    out[pos] = byte;
    return 1;
}

int ofp_frame_encode(uint8_t seq, uint8_t cmd, const uint8_t *payload, size_t payload_len,
                     uint8_t *out, size_t out_cap)
{
    if (out == NULL || payload_len > OFP_MAX_PAYLOAD || (payload == NULL && payload_len > 0))
    {
        return -1;
    }

    /* Build the un-stuffed body: LEN(2) | SEQ | CMD | PAYLOAD | CRC(2). */
    uint8_t body[OFP_MAX_BODY];
    uint16_t len = (uint16_t)(2u + payload_len); /* SEQ + CMD + PAYLOAD */
    body[0] = (uint8_t)(len >> 8);
    body[1] = (uint8_t)(len & 0xFFu);
    body[2] = seq;
    body[3] = cmd;
    for (size_t i = 0; i < payload_len; i++)
    {
        body[4 + i] = payload[i];
    }
    /* CRC covers exactly SEQ+CMD+PAYLOAD, the span LEN counts (§1). */
    uint16_t crc = ofp_crc16(&body[2], (size_t)len);
    body[4 + payload_len] = (uint8_t)(crc >> 8);
    body[5 + payload_len] = (uint8_t)(crc & 0xFFu);
    size_t body_len = 6u + payload_len; /* LEN(2)+SEQ+CMD+PAYLOAD+CRC(2) */

    /* Emit STX, stuffed body, ETX. */
    if (out_cap < 1)
    {
        return -1;
    }
    out[0] = OFP_STX;
    size_t pos = 1;
    for (size_t i = 0; i < body_len; i++)
    {
        int n = stuff_into(out, out_cap, pos, body[i]);
        if (n < 0)
        {
            return -1;
        }
        pos += (size_t)n;
    }
    if (pos + 1 > out_cap)
    {
        return -1;
    }
    out[pos] = OFP_ETX;
    pos += 1;
    return (int)pos;
}

void ofp_frame_decoder_init(ofp_frame_decoder *d)
{
    d->body_len = 0;
    d->in_frame = 0;
    d->esc = 0;
    d->overflow = 0;
    d->seq = 0;
    d->cmd = 0;
    d->payload = NULL;
    d->payload_len = 0;
    d->last_error = OFP_ERR_NONE;
}

static ofp_frame_status finish_frame(ofp_frame_decoder *d)
{
    d->in_frame = 0;
    if (d->overflow || d->esc)
    {
        d->last_error = OFP_ERR_BAD_FRAME;
        return OFP_FRAME_ERR;
    }
    /* Minimum body: LEN(2) + SEQ + CMD + CRC(2) = 6, i.e. empty payload. */
    if (d->body_len < 6)
    {
        d->last_error = OFP_ERR_BAD_FRAME;
        return OFP_FRAME_ERR;
    }
    uint16_t len = (uint16_t)(((uint16_t)d->body[0] << 8) | d->body[1]);
    /* LEN counts SEQ+CMD+PAYLOAD; total body = LEN + LEN-prefix(2) + CRC(2). */
    if ((size_t)len + 4u != d->body_len || len < 2u)
    {
        d->last_error = OFP_ERR_BAD_FRAME;
        return OFP_FRAME_ERR;
    }
    uint16_t got = (uint16_t)(((uint16_t)d->body[2 + len] << 8) | d->body[3 + len]);
    uint16_t want = ofp_crc16(&d->body[2], (size_t)len);
    if (got != want)
    {
        d->last_error = OFP_ERR_BAD_CRC;
        return OFP_FRAME_ERR;
    }
    d->seq = d->body[2];
    d->cmd = d->body[3];
    d->payload = &d->body[4];
    d->payload_len = (size_t)(len - 2u);
    d->last_error = OFP_ERR_NONE;
    return OFP_FRAME_READY;
}

ofp_frame_status ofp_frame_feed(ofp_frame_decoder *d, uint8_t byte)
{
    if (byte == OFP_STX)
    {
        /* STX is never stuffed, so a raw STX always starts a fresh frame — even mid-frame,
         * which resynchronises after a truncated one. */
        d->body_len = 0;
        d->in_frame = 1;
        d->esc = 0;
        d->overflow = 0;
        return OFP_FRAME_NONE;
    }
    if (!d->in_frame)
    {
        return OFP_FRAME_NONE; /* junk between frames */
    }
    if (byte == OFP_ETX)
    {
        return finish_frame(d);
    }
    uint8_t value = byte;
    if (d->esc)
    {
        value = (uint8_t)(byte ^ OFP_STUFF_XOR);
        d->esc = 0;
    }
    else if (byte == OFP_ESC)
    {
        d->esc = 1;
        return OFP_FRAME_NONE;
    }
    if (d->body_len >= OFP_MAX_BODY)
    {
        d->overflow = 1; /* keep consuming until ETX, then report error */
        return OFP_FRAME_NONE;
    }
    d->body[d->body_len++] = value;
    return OFP_FRAME_NONE;
}
