# 0022 — An approval is a pre-authorisation; settlement is for the delivered value

## Context

Phase 5's `TransactionService.CompleteApprovalAsync` settled immediately: approve, publish the
settlement, mark `Completed`. With no dispenser on the path there was nothing else it could do —
the authorised amount was the only amount that existed.

Now the dispenser reports what it actually delivered ([0021](0021-firmware-pumps-on-the-orchestration-path.md)),
and settling the authorised amount would over-charge every customer who did not fill to the
preset. A forecourt pre-authorises, meters, and settles the delivery.

## Decision

**Add `SiteOptions.SettleOnDispenseComplete`, set automatically when the pump fleet is running.**

- When set, an online approval stops after the `Approved` write (POINT 3). The transaction stays
  open while fuel flows.
- `PumpFleet` handles `DISPENSE_COMPLETE` and calls `TransactionService.CompleteFuellingAsync`,
  which settles the delivered value.
- When unset, Phase 5 behaviour is unchanged, so every existing test and the Phase 5 demo keep
  their meaning.

Three rules make `CompleteFuellingAsync` safe:

- **Only an `Approved` transaction is settled.** The firmware re-emits `DISPENSE_COMPLETE` until
  it is acked (`docs/protocol-pump.md` §4.4), so a repeat must be a no-op — and it is, because the
  first call moved the record to `Completed`.
- **A delivery above the authorisation settles the authorisation.** The preset is what stops the
  pump, so a larger figure means the meter is wrong; the customer is charged what was authorised
  and the anomaly is logged.
- **An offline approval is left alone.** It is its own durable replay queue; settling it locally
  would lose the acquirer confirmation it is waiting for.

## Consequences

- The window between `Approved` and `Completed` is now as long as a fuelling, not milliseconds.
  Recovery already covers it: `ResumeAsync` finds an online `Approved` record and resumes
  settlement, so a crash mid-fuelling settles on restart.
- A pump that is authorised and never used leaves an open `Approved` record until the operator
  cancels or the process restarts. There is no automatic authorisation timeout — noted as a known
  weakness in the Phase 6 walkthrough.
- The end-of-day view becomes meaningful: outstanding value is now genuinely outstanding.

## Alternatives considered

- **Always settle on dispense complete.** Simpler — one behaviour, no flag. It would silently
  change the Phase 5 tests and demo, whose whole subject is the money lifecycle, and it would
  leave a controller with no dispensers unable to ever settle.
- **Send a separate ISO 8583 completion/advice message for the delivered amount.** What a real
  acquirer link does. It needs a message type, a dialect entry and host-side handling that the
  simulator does not have, and CLAUDE.md section 11 forbids inventing field definitions. Left for
  a later phase rather than guessed at.
- **Authorise for the delivered amount after the fact.** Not authorisation at all: the customer
  would be dispensing fuel against no approval.
