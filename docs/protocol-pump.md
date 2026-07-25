# OFP-1 — the OpenForecourt Pump Protocol

**Status:** normative for Phase 4 onward. Code must follow this document; if the two
disagree, the document is wrong and both get fixed.

This is an **original protocol**, *inspired by* forecourt device standards such as IFSF
and the general shape of dispenser ↔ controller links (framed, sequence-numbered,
CRC-protected, with unsolicited event reporting). **It is not an implementation of any
proprietary specification.** Every value below is chosen for this project.

The pump manager (C#, site side) is the **master**: it issues commands and the pump
firmware (C, device side) answers. The pump is also allowed to speak *unsolicited* —
nozzle and flow events happen on the customer's schedule, not the controller's.

---

## 1. Framing

Every frame on the wire is:

```
+-----+--------+-----+-----+-------------+---------+-----+
| STX | LEN(2) | SEQ | CMD | PAYLOAD     | CRC16   | ETX |
| 02  | 2 by.  | 1   | 1   | LEN-2 bytes | 2 bytes | 03  |
+-----+--------+-----+-----+-------------+---------+-----+
      \________________ CRC-covered / LEN-counted _____/
```

- **STX** = `0x02`, **ETX** = `0x03`. Frame delimiters.
- **LEN** — unsigned 16-bit, **big-endian**. Counts the bytes of `SEQ + CMD + PAYLOAD`
  (i.e. `2 + payload_length`). It does **not** include STX, LEN itself, the CRC, or ETX.
  `LEN` is the *logical* length, computed before byte-stuffing (§1.2).
- **SEQ** — 1 byte, `0x00`–`0xFF`, wraps. See §4.
- **CMD** — 1 byte command/event code. See §2.
- **PAYLOAD** — `LEN − 2` bytes, command-specific (§3). May be empty.
- **CRC16** — 2 bytes, **big-endian**, computed over `SEQ + CMD + PAYLOAD` (exactly the
  span `LEN` counts). Algorithm in §1.1.
- All multi-byte integers in this protocol are **big-endian** (network order).

### 1.1 CRC-16/CCITT-FALSE

We define the CRC as **CRC-16/CCITT-FALSE**:

| Parameter | Value |
|---|---|
| Width | 16 bits |
| Polynomial | `0x1021` |
| Init | `0xFFFF` |
| Input reflected | no |
| Output reflected | no |
| XOR out | `0x0000` |

**Canonical check value:** the CRC of the ASCII string `"123456789"` (bytes
`31 32 33 34 35 36 37 38 39`) is **`0x29B1`**. Both the C and the C# implementations
assert this vector in their tests; a build that computes anything else is wrong. This is
what makes "CRC16-CCITT" unambiguous here — the phrase alone names at least three
different algorithms, so we pin the one we mean by its published check value rather than
trusting the name.

### 1.2 Byte stuffing

`STX`/`ETX` must be unambiguous, so any occurrence of a reserved byte **inside the frame
body** (between STX and ETX — that is, within LEN, SEQ, CMD, PAYLOAD or CRC) is escaped
before transmission and un-escaped on receipt.

- **ESC** = `0x10` (DLE).
- Reserved bytes: `0x02` (STX), `0x03` (ETX), `0x10` (ESC).
- To stuff a reserved byte `B`: emit `0x10` then `B XOR 0x20`.
  - `0x02` → `10 22`
  - `0x03` → `10 23`
  - `0x10` → `10 30`
- To un-stuff: on reading `0x10`, take the next byte and XOR it with `0x20`.

Stuffing is a pure transport concern applied **after** LEN and CRC are computed and
removed **before** they are checked. LEN and CRC therefore always describe the logical
(un-stuffed) bytes. STX and ETX themselves are never stuffed — they are the only raw
`0x02`/`0x03` on the wire, which is exactly how the receiver finds frame boundaries.

---

## 2. Command and event codes

| Code | Name | Direction | Kind |
|---|---|---|---|
| `0x01` | `AUTHORISE` | manager → pump | command |
| `0x02` | `CANCEL_AUTH` | manager → pump | command |
| `0x03` | `STATUS_REQ` | manager → pump | command |
| `0x04` | `TOTALS_REQ` | manager → pump | command |
| `0x05` | `SUSPEND` | manager → pump | command |
| `0x06` | `RESUME` | manager → pump | command |
| `0x20` | `NOZZLE_UP` | pump → manager | unsolicited event |
| `0x21` | `NOZZLE_DOWN` | pump → manager | unsolicited event |
| `0x22` | `FLOW_UPDATE` | pump → manager | unsolicited event |
| `0x23` | `DISPENSE_COMPLETE` | pump → manager | unsolicited event |
| `0x24` | `FAULT` | pump → manager | unsolicited event |
| `0x7F` | `NAK` | pump → manager | command response |
| `0x81`..`0x86` | `ACK` of command `0x01`..`0x06` | pump → manager | command response |

**Responses.** A command `C` is answered by the pump with either:
- an **ACK**: `CMD = C | 0x80` (so `AUTHORISE`→`0x81`, `STATUS_REQ`→`0x83`, …), carrying
  any response payload (§3), echoing the command's `SEQ`; or
- a **NAK**: `CMD = 0x7F`, payload = one error code byte (§5), echoing the command's `SEQ`.

Every manager command is answered by exactly one ACK or NAK. Events are **not**
individually acknowledged at the frame level (§4.3).

---

## 3. Payloads

All integers big-endian. "mL" = millilitres. "value" = currency minor units (e.g. pence).

### 3.1 `AUTHORISE` (command)
| Offset | Size | Field |
|---|---|---|
| 0 | 1 | preset mode: `0`=none (full tank), `1`=volume limit, `2`=value limit |
| 1 | 4 | limit: mL (mode 1) or minor units (mode 2); ignored for mode 0 |

- ACK `0x81` payload: empty. NAK if not in a state that permits authorisation (§5).

### 3.2 `CANCEL_AUTH`, `SUSPEND`, `RESUME` (commands)
- Empty payload. ACK (`0x82`/`0x85`/`0x86`) empty payload, or NAK.

### 3.3 `STATUS_REQ` (command)
- Empty payload. ACK `0x83` payload:
| Offset | Size | Field |
|---|---|---|
| 0 | 1 | current FSM state (§6 numeric codes) |
| 1 | 4 | current dispense volume (mL) |
| 5 | 4 | current dispense value (minor units) |

### 3.4 `TOTALS_REQ` (command)
- Empty payload. ACK `0x84` payload (the non-resettable lifetime totalizer, §7):
| Offset | Size | Field |
|---|---|---|
| 0 | 8 | lifetime volume (mL) |
| 8 | 8 | lifetime value (minor units) |

### 3.5 `FLOW_UPDATE` (event)
| Offset | Size | Field |
|---|---|---|
| 0 | 4 | cumulative volume this dispense (mL) |
| 4 | 4 | cumulative value this dispense (minor units) |

### 3.6 `DISPENSE_COMPLETE` (event)
| Offset | Size | Field |
|---|---|---|
| 0 | 4 | final volume (mL) |
| 4 | 4 | final value (minor units) |

### 3.7 `NOZZLE_UP` / `NOZZLE_DOWN` (events)
- Empty payload.

### 3.8 `FAULT` (event)
| Offset | Size | Field |
|---|---|---|
| 0 | 1 | fault code (§5) |

---

## 4. Sequence numbers, timeouts, retries

### 4.1 Two independent sequence spaces
- **Command SEQ** — owned by the manager. It increments for each *new* command. A
  retransmission of an unanswered command reuses the **same** SEQ.
- **Event SEQ** — owned by the pump. It increments for each *new* unsolicited event.
- The two spaces are independent: a command SEQ and an event SEQ may collide numerically
  and mean nothing to each other. Direction disambiguates.
- An ACK/NAK echoes the **command** SEQ it answers (it is not a new sequence value).

### 4.2 Command timeout and retry (manager side)
- After sending a command with SEQ `N`, the manager arms a timer `T_RESPONSE` (default
  **1000 ms**, from config, measured on `IClock`).
- If no ACK/NAK echoing `N` arrives before `T_RESPONSE`, the manager **retransmits the
  identical frame** (same SEQ `N`) and re-arms the timer.
- Up to `MAX_RETRIES` (default **3**) retransmissions. After the last one times out, the
  command **fails** and the link is declared down: the transport begins reconnecting with
  exponential backoff and jitter (§8).

### 4.3 Duplicate handling (pump side) — idempotency
Retransmission means the pump can receive the same command twice (its first ACK was lost).
The firmware therefore keeps the **last processed command SEQ and its cached response**:
- If an arriving command's SEQ **equals** the last processed SEQ, the pump **re-sends the
  cached response without re-executing the side effect**. This is essential for
  `AUTHORISE` — a lost ACK must never cause a second authorisation.
- Otherwise the command is new: process it, cache `(SEQ, response)`, reply.

This makes every command **idempotent** under at-most-once *execution* with at-least-once
*delivery*.

### 4.4 Event reliability
- `FLOW_UPDATE` is **best-effort**: a lost update is superseded by the next one, so it is
  never retransmitted. The manager treats event SEQ gaps on FLOW_UPDATE as tolerable.
- `DISPENSE_COMPLETE` is **critical** and delivered at-least-once *without a new frame
  type*: the firmware stays in `COMPLETE` (§6) and **re-emits `DISPENSE_COMPLETE` every
  `T_EVENT_REPEAT` (default 1000 ms)** until the manager moves it out of `COMPLETE` with a
  `CANCEL_AUTH` (the settle-ack) or a new `AUTHORISE`. Each re-emission reuses the **same
  event SEQ**, so the manager dedups on event SEQ and acts once.
- `NOZZLE_UP`/`NOZZLE_DOWN`/`FAULT` are sent once; the manager can always recover the
  current truth with `STATUS_REQ`.

---

## 5. Error and fault codes

**Command error codes** (NAK payload byte):
| Code | Name | Meaning |
|---|---|---|
| `0x00` | `ERR_NONE` | (not used in a NAK; success is an ACK) |
| `0x01` | `ERR_BAD_CRC` | CRC check failed — frame dropped, not NAK'd (see note) |
| `0x02` | `ERR_BAD_FRAME` | LEN/stuffing/length mismatch — frame dropped (see note) |
| `0x03` | `ERR_ILLEGAL_STATE` | command not valid in the current FSM state |
| `0x04` | `ERR_UNKNOWN_CMD` | unrecognised command code |
| `0x05` | `ERR_PRESET_INVALID` | AUTHORISE preset mode/limit invalid |

> **Note.** `ERR_BAD_CRC`/`ERR_BAD_FRAME` describe *corrupt* frames. A corrupt frame has
> no trustworthy SEQ to echo, so the pump **silently drops** it; the manager recovers by
> timeout+retry (§4.2). These codes exist for logging/telemetry, not as NAK payloads.
> A NAK is only sent for a *well-formed* frame the pump refuses (`0x03`–`0x05`).

**Fault codes** (`FAULT` event payload byte, and STATUS when latched):
| Code | Name | Cause |
|---|---|---|
| `0x00` | `FAULT_NONE` | no fault |
| `0x10` | `FAULT_NOZZLE_UNEXPECTED` | nozzle lifted with no authorisation |
| `0x11` | `FAULT_FLOW_WHILE_IDLE` | flow pulses with no active dispense |
| `0x12` | `FAULT_WATCHDOG` | run-loop watchdog not kicked in time |
| `0x13` | `FAULT_METER_STALL` | dispensing but no pulses for `T_STALL` |

Faults **latch**: once faulted the pump stays in `FAULT` and NAKs operational commands
until cleared by `CANCEL_AUTH` (the recovery/clear command), which returns it to `IDLE`.

---

## 6. Dispenser state machine

Numeric state codes (used in STATUS payload and the C/C# FSM tables):

| Code | State | Meaning |
|---|---|---|
| `0` | `IDLE` | available; no authorisation |
| `1` | `AUTHORISED` | preset loaded; waiting for nozzle lift |
| `2` | `DISPENSING` | nozzle up; fuel flowing; totalizer counting |
| `3` | `COMPLETE` | nozzle holstered / limit reached; final totals held |
| `4` | `SUSPENDED` | dispensing paused by the controller |
| `5` | `FAULT` | latched fault; awaits clear |

### 6.1 Transition table

Events are frames received (`AUTHORISE`, `CANCEL_AUTH`, `SUSPEND`, `RESUME`,
`NOZZLE_UP`, `NOZZLE_DOWN`) and **internal** triggers (`FLOW` = a flow pulse batch,
`LIMIT` = preset limit reached, `STALL`/`WDOG` = fault detectors). `STATUS_REQ` and
`TOTALS_REQ` are pure reads: valid in **every** state, never change state, never appear
below.

| From \ Event | AUTHORISE | CANCEL_AUTH | NOZZLE_UP | NOZZLE_DOWN | FLOW | LIMIT | SUSPEND | RESUME | fault detector |
|---|---|---|---|---|---|---|---|---|---|
| **IDLE** | →AUTHORISED (load preset) | →IDLE (no-op ACK) | →FAULT `NOZZLE_UNEXPECTED` | illegal | →FAULT `FLOW_WHILE_IDLE` | illegal | illegal | illegal | →FAULT |
| **AUTHORISED** | illegal | →IDLE | →DISPENSING | illegal | illegal | illegal | illegal | illegal | →FAULT |
| **DISPENSING** | illegal | illegal | illegal | →COMPLETE (emit DISPENSE_COMPLETE) | →DISPENSING (accumulate, emit FLOW_UPDATE) | →COMPLETE (emit DISPENSE_COMPLETE) | →SUSPENDED (halt flow) | illegal | →FAULT |
| **COMPLETE** | →AUTHORISED (load preset, reset dispense totals) | →IDLE (settle-ack) | illegal | illegal | illegal | illegal | illegal | illegal | →FAULT |
| **SUSPENDED** | illegal | →IDLE | illegal | →COMPLETE (emit DISPENSE_COMPLETE) | ignored (no accumulate) | illegal | illegal | →DISPENSING | →FAULT |
| **FAULT** | illegal | →IDLE (clear) | illegal | illegal | ignored | illegal | illegal | illegal | stay FAULT |

- **illegal** = the transition must not occur. For a *command* frame it is answered
  `NAK ERR_ILLEGAL_STATE` and the state is unchanged. For an *internal* trigger that
  cannot legitimately happen (e.g. `LIMIT` while `IDLE`) it is a firmware assertion the
  simulation never fires; the table lists it only for completeness of the C test matrix.
- **ignored** = accepted but no state change and no side effect (e.g. stray flow while
  suspended is swallowed, not faulted, because the pause is deliberate).
- Every operational command that lands on a legal cell is answered with an **ACK**;
  `STATUS_REQ`/`TOTALS_REQ` are always ACK'd with their read payload.

---

## 7. Totalizer, flow meter, preset, watchdog

- **Totalizer** — two 64-bit lifetime counters (volume mL, value minor units). They are
  **monotonic and non-resettable**: every completed dispense's volume/value is added,
  nothing ever subtracts. In a real dispenser this is the legally-sealed register.
- **Flow meter simulation** — configurable **pulses per litre** (`PULSES_PER_LITRE`,
  default 1000, i.e. 1 pulse = 1 mL). The host HAL drives pulses on a timer to simulate
  fuel flow; each pulse batch is a `FLOW` event that accumulates `mL` and recomputes value
  from a configured **unit price** (minor units per litre).
- **Preset** — modes in §3.1. In mode 1/2 the firmware stops the dispense (`LIMIT`) the
  instant the accumulated volume/value reaches the limit, clamping to exactly the limit.
- **Watchdog** — the run loop kicks a software watchdog each iteration. If it is not
  kicked within `T_WDOG` the pump raises `FAULT_WATCHDOG`. On the host build this is a
  counter that tests can force; on the target it maps to the STM32 IWDG.
- **Meter stall** — while `DISPENSING`, if no pulse arrives for `T_STALL` and the preset
  limit is not yet reached, raise `FAULT_METER_STALL` (a stuck meter or empty tank).

Defaults (all overridable via config / HAL):
`T_RESPONSE`=1000 ms, `MAX_RETRIES`=3, `T_EVENT_REPEAT`=1000 ms, `T_STALL`=3000 ms,
`T_WDOG`=500 ms, `PULSES_PER_LITRE`=1000.

---

## 8. Reconnect

The transport, not the FSM, owns reconnection. On link loss it retries with **exponential
backoff and jitter**: delay `= min(BASE * 2^attempt, CAP)` then a random `±JITTER%`.
Defaults `BASE`=200 ms, `CAP`=5 s, `JITTER`=20%. The FSM state is preserved across a
reconnect (the pump keeps dispensing); on reconnect the manager issues `STATUS_REQ` to
resynchronise, and the pump's `DISPENSE_COMPLETE` re-emission (§4.4) covers any completion
that happened while the link was down.

---

## 9. Worked example — one successful fuelling

A £10-value-preset fuelling of pump, ending at the limit. Bytes shown are the **logical**
frame (pre-stuffing); none of these particular bytes need stuffing. All hex.

**Unit price** 150 minor units/litre (£1.50/L), value preset £10.00 = `0x000003E8`
minor units.

### 9.1 Manager → pump: `AUTHORISE`, value preset £10.00, SEQ `0x07`
Body (`SEQ CMD PAYLOAD`): `07 01 02 000003E8`
- `SEQ`=`07`, `CMD`=`01` (AUTHORISE), preset mode `02` (value), limit `000003E8`.
- `LEN` = 2 (SEQ+CMD) + 5 (payload) = `0x0007`.
- CRC-16/CCITT-FALSE over `07 01 02 00 00 03 E8` = `0xC0DC`. *(computed by the codec; the
  golden-frame test pins it — see §1.1 for how the algorithm itself is pinned.)*

Frame: `02 0007 07 01 02 000003E8 C0DC 03`
→ `02 00 07 07 01 02 00 00 03 E8 C0 DC 03`

### 9.2 Pump → manager: `ACK` of AUTHORISE, SEQ `0x07`
Body: `07 81` — `LEN`=`0x0002`, CRC over `07 81`.
Pump transitions `IDLE → AUTHORISED`.

### 9.3 Pump → manager: `NOZZLE_UP` event, event SEQ `0x00`
Body: `00 20` — customer lifts nozzle. Pump `AUTHORISED → DISPENSING`.

### 9.4 Pump → manager: `FLOW_UPDATE` events (best-effort), event SEQ `0x01`, `0x02`, …
E.g. at 3.000 L dispensed: volume `0x00000BB8` mL (3000), value `0x000001C2` (3.000 L ×
150 = 450 minor units). Body: `01 22 00000BB8 000001C2`. These stream as fuel flows.

### 9.5 Preset limit reached
When accumulated value hits £10.00 the firmware fires `LIMIT`, clamps volume to
6.666 L (10000 ÷ 150 × 1000 = 6666 mL, `0x00001A0A`), stops flow, and emits
`DISPENSE_COMPLETE` event SEQ `0x0A`: body `0A 23 00001A0A 000003E8`
(volume 6666 mL, value 1000 minor units). Pump `DISPENSING → COMPLETE`.
It re-emits this same frame every `T_EVENT_REPEAT` until acked.

### 9.6 Manager settles online, then → pump: `CANCEL_AUTH`, SEQ `0x08`
Body: `08 02` — the settle-ack. Pump `COMPLETE → IDLE`, lifetime totalizer += (6666 mL,
1000 minor units). Pump ACKs `0x82` SEQ `08`. Fuelling complete.

---

## 10. Known simplifications

- One preset dimension at a time (volume **or** value), not both, and no price tiers.
- No message authentication (MAC) on the pump link — the backlog notes a DUKPT-MAC'd host
  link; a device-local UART link in a sealed forecourt is a different threat surface. This
  is a documented simplification, not a claim the link is secure against a wire attacker.
- The event channel carries no windowed flow control; `FLOW_UPDATE` best-effort + critical
  event re-emission is sufficient for a single dispense's data.
- SEQ is 8-bit and wraps at 256; at one command per second that is safe, and duplicate
  detection is single-deep (last SEQ only), which matches at-most-one-outstanding-command.
