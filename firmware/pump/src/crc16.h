/* CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, xorout 0).
 * docs/protocol-pump.md §1.1 pins the algorithm by its check value: crc("123456789")
 * == 0x29B1. The C test and the C# codec both assert that vector. */
#ifndef OFP_CRC16_H
#define OFP_CRC16_H

#include <stddef.h>
#include <stdint.h>

uint16_t ofp_crc16(const uint8_t *data, size_t len);

#endif /* OFP_CRC16_H */
