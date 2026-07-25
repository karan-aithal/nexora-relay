/* OFP-1 pump protocol constants. See docs/protocol-pump.md — the document is normative.
 * These names and values are chosen for this project; OFP-1 is an original protocol
 * inspired by forecourt standards (IFSF), not an implementation of any proprietary spec. */
#ifndef OFP_PROTOCOL_H
#define OFP_PROTOCOL_H

/* Framing bytes (docs/protocol-pump.md §1). */
#define OFP_STX 0x02u
#define OFP_ETX 0x03u
#define OFP_ESC 0x10u /* DLE; stuffed byte = ESC, (byte ^ 0x20) */
#define OFP_STUFF_XOR 0x20u

/* Command codes, manager -> pump (§2). */
#define OFP_CMD_AUTHORISE 0x01u
#define OFP_CMD_CANCEL 0x02u
#define OFP_CMD_STATUS 0x03u
#define OFP_CMD_TOTALS 0x04u
#define OFP_CMD_SUSPEND 0x05u
#define OFP_CMD_RESUME 0x06u

/* Event codes, pump -> manager, unsolicited (§2). */
#define OFP_EVT_NOZZLE_UP 0x20u
#define OFP_EVT_NOZZLE_DOWN 0x21u
#define OFP_EVT_FLOW_UPDATE 0x22u
#define OFP_EVT_DISPENSE_COMPLETE 0x23u
#define OFP_EVT_FAULT 0x24u

/* Responses (§2): ACK = command | 0x80; NAK = 0x7F carrying one error byte. */
#define OFP_ACK_MASK 0x80u
#define OFP_NAK 0x7Fu

/* Command error codes, NAK payload (§5). */
#define OFP_ERR_NONE 0x00u
#define OFP_ERR_BAD_CRC 0x01u
#define OFP_ERR_BAD_FRAME 0x02u
#define OFP_ERR_ILLEGAL_STATE 0x03u
#define OFP_ERR_UNKNOWN_CMD 0x04u
#define OFP_ERR_PRESET_INVALID 0x05u

/* Fault codes, FAULT payload (§5). */
#define OFP_FAULT_NONE 0x00u
#define OFP_FAULT_NOZZLE_UNEXPECTED 0x10u
#define OFP_FAULT_FLOW_WHILE_IDLE 0x11u
#define OFP_FAULT_WATCHDOG 0x12u
#define OFP_FAULT_METER_STALL 0x13u

/* Preset modes, AUTHORISE payload byte 0 (§3.1). */
#define OFP_PRESET_NONE 0x00u
#define OFP_PRESET_VOLUME 0x01u
#define OFP_PRESET_VALUE 0x02u

/* Dispenser FSM state codes (§6). */
#define OFP_STATE_IDLE 0u
#define OFP_STATE_AUTHORISED 1u
#define OFP_STATE_DISPENSING 2u
#define OFP_STATE_COMPLETE 3u
#define OFP_STATE_SUSPENDED 4u
#define OFP_STATE_FAULT 5u

/* Fixed buffer bounds (no dynamic allocation, CLAUDE.md §6 C rules). Largest payload is
 * the TOTALS response (16 bytes); 32 leaves headroom. Encoded worst case is every body
 * byte stuffed: 1 + 2*(LEN(2)+SEQ(1)+CMD(1)+PAYLOAD+CRC(2)) + 1. */
#define OFP_MAX_PAYLOAD 32u
#define OFP_MAX_BODY (2u + 1u + 1u + OFP_MAX_PAYLOAD + 2u) /* LEN+SEQ+CMD+PAYLOAD+CRC */
#define OFP_MAX_FRAME (1u + (2u * OFP_MAX_BODY) + 1u)

#endif /* OFP_PROTOCOL_H */
