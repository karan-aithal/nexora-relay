# Phase 4 walkthrough — Pump firmware, transports, and the pump manager

## 1. Design rationale

Phase 4 builds the dispenser side of the forecourt: real embedded C firmware that drives a
pump's state machine, and a C# pump manager that talks to it over three interchangeable
transports. The shape is deliberate and follows CLAUDE.md:

- **Protocol first.** `docs/protocol-pump.md` (OFP-1) was written and committed *before any pump
  code* (ADR 0013). It fixes framing, the command/event set, the FSM transition table, the
  sequence/timeout/retry rules, and a CRC-verified worked byte trace. The code follows the
  document; where they ever disagree the document wins and both get fixed.
- **One portable C core behind a six-function HAL** (ADR 0014). The same `firmware/pump/src/`
  runs against a host HAL (UART → TCP socket) and compiles for an STM32 target HAL that CI
  cross-compiles but never runs. No dynamic allocation, no `printf`, no blocking, an explicit
  FSM table — the embedded mirror of the ports-and-adapters thesis.
- **One actor per pump on the C# side** (ADR 0015). Each `PumpSession` owns two channels (inbound
  frames, outbound commands) drained by a single loop, so there are **no locks** around pump
  state. Command/response is correlated by sequence number with `IClock`-measured timeout and
  retry; a mirrored copy of the dispenser FSM flags any illegal transition the firmware reports.
- **Three transports, one port.** `TcpPumpTransport`, `SerialPumpTransport` (Windows/com0com) and
  `InProcPumpTransport` (CI) all implement `IPumpTransport`; swapping one for another is a
  configuration change, not a code change.

The payoff is a system where the C firmware and the C# manager agree on the wire *by test* — the
same golden frame is asserted byte-for-byte in both languages — and where eight pumps run
concurrently with no shared-state locking.

## 2. The three hardest parts, line by line

### 2.1 Byte-stuffing, and why the golden frame is 15 bytes, not 13

The frame is `STX | LEN(2) | SEQ | CMD | PAYLOAD | CRC16 | ETX`. The subtlety is that `STX`
(`0x02`) and `ETX` (`0x03`) can legitimately appear *inside* the body, so they must be escaped —
but `LEN` and the CRC have to describe the **logical** (un-stuffed) bytes or the two sides can
never agree.

In `firmware/pump/src/frame.c`, `ofp_frame_encode` builds the logical body first:

```c
uint16_t len = (uint16_t)(2u + payload_len);   /* LEN counts SEQ + CMD + PAYLOAD */
body[0] = (uint8_t)(len >> 8);
body[1] = (uint8_t)(len & 0xFFu);
body[2] = seq;
body[3] = cmd;
/* ... payload ... */
uint16_t crc = ofp_crc16(&body[2], (size_t)len);   /* CRC covers exactly SEQ+CMD+PAYLOAD */
```

Only *then* does it wrap the body with `STX`, stuff each body byte, and append `ETX`:

```c
out[0] = OFP_STX;
for (size_t i = 0; i < body_len; i++) {
    int n = stuff_into(out, out_cap, pos, body[i]);   /* 0x02/0x03/0x10 -> ESC, b^0x20 */
    pos += (size_t)n;
}
out[pos++] = OFP_ETX;
```

Now the worked example bites. The AUTHORISE frame from `docs §9.1` has SEQ `07`, CMD `01`,
payload `02 000003E8` (preset mode `0x02` = value, amount `0x000003E8`). The logical body is
`00 07 07 01 02 00 00 03 E8 C0 DC` — and it **contains `0x02` (the preset mode) and `0x03` (a
byte of the amount)**. Both are reserved, so on the wire they become `10 22` and `10 23`:

```
logical:  02 00 07 07 01 02 00 00 03 E8 C0 DC 03      (would be 13 bytes)
on wire:  02 00 07 07 01 10 22 00 00 10 23 E8 C0 DC 03 (15 bytes, stuffed)
```

The first draft of the doc claimed "none of these bytes need stuffing" — the C test caught it
immediately (`got 15, want 13`). The doc was wrong; both the doc and the test now carry the real
15-byte frame, and it doubles as the stuffing demonstration. That 15-byte sequence is the
cross-language anchor: `firmware/pump/test/test_frame.c` and
`tests/…/OfpCodecTests.Encodes_the_golden_authorise_frame_byte_for_byte` assert the identical
bytes, so a stuffing or CRC disagreement between C and C# fails a test rather than corrupting a
transaction in the field.

The decoder (`ofp_frame_feed`) is the mirror image and has one deliberately clever line: a **raw
`STX` always starts a fresh frame**, even mid-frame —

```c
if (byte == OFP_STX) { d->body_len = 0; d->in_frame = 1; d->esc = 0; d->overflow = 0; return OFP_FRAME_NONE; }
```

Because `STX` is the only unescaped `0x02` on the wire, a receiver that lost sync on a truncated
frame resynchronises automatically on the next real frame start. The resync test (`junk + STX +
0x99`, then a clean frame) proves it, in both languages.

### 2.2 Idempotent AUTHORISE, and the lock-free timeout/retry loop

Retransmission plus a lost ACK means the firmware can receive the *same* command twice. For most
commands that is harmless; for AUTHORISE it must never authorise twice. The firmware keeps the
last command's SEQ and its cached response (`firmware/pump/src/pump.c`, `handle_frame`):

```c
if (p->have_last_cmd && seq == p->last_cmd_seq) {
    hal_uart_write(p->last_resp, p->last_resp_len);   /* re-send cached response, no re-execute */
    return;
}
```

That single guard makes every command idempotent under at-most-once *execution* with
at-least-once *delivery*. `test_pump.c` sends AUTHORISE twice with the same SEQ and asserts the
state is unchanged and no second event was emitted (`event_seq == 0`).

The manager side is the counterpart, and it is where "no locks around shared mutable pump state"
lives. `PumpSession.ProcessLoopAsync` is a single loop that owns everything. The core of it:

```csharp
while (_inbound.Reader.TryRead(out var msg)) HandleInbound(msg);   // 1. drain inbound first
if (_pending is null && _outbound.Reader.TryRead(out var cmd))
    { await StartCommandAsync(cmd, ct); continue; }                // 2. start a command if idle
// 3. wait for inbound, a new command, or (if pending) the deadline
var inReady  = _inbound.Reader.WaitToReadAsync(ct).AsTask();
var deadline = _clock.Delay(remaining, ct);
await Task.WhenAny(inReady, deadline);
```

Two things make this correct. First, **inbound is drained before the timeout is checked**, so a
response that arrived in the same wake-up completes the command and cancels the timeout — the
`WhenAny(inbound, deadline)` race can never spuriously time out a command whose ACK is already in
the channel. Second, because this loop is the *only* thing that reads `_pending`, `_nextSeq` and
the mirrored state, none of them needs a lock. `StartCommandAsync` allocates the SEQ, builds the
body once, and stores it so `OnTimeoutAsync` can retransmit *the identical bytes* (same SEQ) up to
`MaxRetries` before failing with `CommandStatus.Timeout`.

Testing timeouts without waiting is done with a fake clock whose `Delay` advances its own `UtcNow`
by the requested interval (`AutoAdvanceClock`). A silent device then produces exactly
`1 + MaxRetries` transmissions and a `Timeout` result, deterministically —
`PumpTimeoutTests.A_silent_pump_causes_exactly_maxretries_retransmissions_then_timeout` asserts the
count is 4.

### 2.3 The mirrored FSM and the DISPENSE_COMPLETE ambiguity

The manager keeps a C# copy of the firmware's transition table (`DispenserFsm`) and advances it on
every acked command and observed event, so a transition the table forbids is flagged as a protocol
violation instead of being trusted (`PumpSession.ApplyFsm`):

```csharp
var r = DispenserFsm.Next(State, ev);
if (r.Outcome == DispenserFsm.Outcome.Illegal) { Log($"… illegal in state {State}"); return false; }
if (r.Outcome == DispenserFsm.Outcome.Legal)   State = r.Next;
return true;
```

The hard case is `DISPENSE_COMPLETE`. The firmware reaches COMPLETE two different ways — the preset
LIMIT fired (auto-stop, no nozzle-down), or the customer holstered (a NOZZLE_DOWN the manager
already saw). The manager cannot tell which from the completion frame alone, so it disambiguates on
its *current mirror state* (`HandleEvent`):

```csharp
case PumpEvent.DispenseComplete:
    if (State == DispenserState.Dispensing)      // auto-stop: apply LIMIT now
        violation = !ApplyFsm(DispenserFsm.Trigger.Limit, "DISPENSE_COMPLETE");
    else if (State != DispenserState.Complete)   // a nozzle-down already moved us to Complete
        { violation = true; detail = $"DISPENSE_COMPLETE in state {State}"; }
    break;
```

The other subtlety is that `DISPENSE_COMPLETE` is **re-emitted with the same event SEQ** until the
manager settles (the at-least-once mechanism, `docs §4.4`), so the manager must suppress duplicates
or it would double-count. One guard does it:

```csharp
if (msg.Seq == _lastEventSeq && msg.Code == _lastEventCode) return;   // dedup re-emitted events
```

`PumpSessionTests.Full_value_preset_fuelling_correlates_commands_and_parses_events` drives the whole
path through a scripted device and asserts the parsed completion is 6.666 L / £10.00 with no
violation; `An_illegal_firmware_transition_is_flagged_as_a_protocol_violation` sends a NOZZLE_DOWN
in Idle and asserts the mirror flags it and does **not** advance.

## 3. What was rejected and why

- **Reproducing an IFSF/vendor dialect** — proprietary and would force `SPEC-UNVERIFIED` guesses;
  an honest original protocol is better than a confidently wrong one (ADR 0013, CLAUDE.md §11).
- **Locks around a shared pump-state object** — the actor/channel model the phase mandates is
  lock-free by construction and simpler to reason about (ADR 0015).
- **A dedicated `Adapters.Tcp` project** — unneeded; TCP client code already lives in the consuming
  project pattern (`HostSimulator`), so the codec + TCP transport live in `PumpManager`.
- **Unity/CMocka for the C tests, a table-driven FSM engine** — both over-engineering; a small
  assert harness and a plain 2-D table literal meet the requirements (ADR 0014).
- **ACKing every event** — FLOW_UPDATE loss is self-correcting; the one critical event is made
  reliable by re-emission through the existing FSM, avoiding a new frame type.
- **Committing the totalizer at settle-ack** — rejected in favour of sealing it at DISPENSE_COMPLETE
  emission, so a lost or late settle-ack can never lose or double-count delivered fuel.

## 4. Self-quiz

1. The golden AUTHORISE frame is 15 bytes on the wire but its logical body is 11 bytes plus
   delimiters. Walk the byte-stuffing that accounts for the difference, and explain why `LEN` and
   the CRC are computed over the un-stuffed body rather than the wire bytes.
2. A lost AUTHORISE **ACK** causes the manager to retransmit. Trace exactly what the firmware does
   on the duplicate, and explain why re-executing AUTHORISE instead of re-sending the cached
   response would be a real bug (not just an inefficiency).
3. In `ProcessLoopAsync`, why is inbound drained *before* the deadline is evaluated? Construct the
   interleaving that would spuriously time out a successful command if the order were reversed.
4. The manager receives `DISPENSE_COMPLETE`. How does it decide whether that is a protocol
   violation, given it cannot see whether the pump stopped on the preset limit or on a nozzle drop?
5. The STM32 target build is compiled but never linked or run in CI. What does that actually prove
   about the portable core, and what does it *not* prove? Why is that the right trade-off here?

## 5. Known weaknesses

- **No link-layer authentication (MAC) on the pump link.** OFP-1 has integrity (CRC) but not
  authenticity; a wire attacker on the dispenser link is out of scope. The backlog notes a
  DUKPT-MAC'd *host* link; a sealed device-local UART is a different threat surface. Documented,
  not claimed secure.
- **Single-deep command window.** One command outstanding per pump; no pipelining. This matches the
  firmware's single-SEQ idempotency cache but caps command throughput per pump.
- **8-bit sequence space.** SEQ wraps at 256; safe at the actual command rate, and duplicate
  detection is last-SEQ-only, which is exactly the at-most-one-outstanding model.
- **A fault mid-dispense does not auto-commit the partial delivery** to the totalizer;
  reconciliation is left to the controller. A production meter would seal partial volume.
- **The STM32 HAL shim is representative, not version-pinned** (`SPEC-UNVERIFIED`): exact Cube
  signatures vary by family. The target build proves the core compiles freestanding for Cortex-M4,
  not that it runs on a specific board.
- **The flow meter and customer are simulated** in the host build; a real dispenser needs pulse
  de-bounce, meter calibration, and slow-flow/prepay handling the simulation does not model.
