# Phase 2 — Card Layer: BER-TLV, Virtual Card, PC/SC, EMV Online Flow

**Goal:** A terminal that runs a real EMV transaction flow against a card you also
wrote, over the genuine Windows PC/SC API.

**Read first:** `CLAUDE.md` section 3 (adapters) and section 11 (spec honesty).

## In scope
`OpenForecourt.Emv`, `OpenForecourt.VirtualCard`, `OpenForecourt.Adapters.Pcsc`,
`OpenForecourt.Adapters.InProc`

## Deliverables

### BER-TLV codec
- Multi-byte tags, multi-byte lengths, constructed vs primitive
- Parse to a navigable tree; write back byte-identically
- A tag dictionary with names for the common tags so traces are readable
- Fuzz-resistant: truncated and malformed input returns errors, never throws or loops

### Virtual card (`OpenForecourt.VirtualCard`)
An APDU responder — the card side of the conversation. Card profiles loaded from JSON
under `tests/testdata/cards/`.

Must handle:
- `SELECT` of PPSE (`2PAY.SYS.DDF01`) and PSE (`1PAY.SYS.DDF01`)
- `SELECT` by AID, returning FCI with tag 6F / A5 structure
- `GET PROCESSING OPTIONS` returning AIP and AFL
- `READ RECORD` driven by the AFL
- `GET DATA` for counters (ATC, last online ATC)
- `GENERATE AC` returning an **ARQC** (online authorisation requested)
- Correct status words: `9000`, `6A82`, `6A86`, `6700`, `6985`

Support at least three profiles: a contact chip card, a contactless card, and a card
that forces a decline.

### Card reader adapters
- `PcscCardReader` — **direct P/Invoke into `winscard.dll`**: `SCardEstablishContext`,
  `SCardListReaders`, `SCardConnect`, `SCardTransmit`, `SCardGetStatusChange`,
  `SCardDisconnect`. Do not use a wrapper library. The point is that this is the real
  production code path.
- `VirtualPcdCardReader` — talks to a `vsmartcard` vpcd virtual reader over TCP, so the
  PC/SC stack itself is exercised with no hardware
- `InProcCardReader` — calls the virtual card directly, for CI on Linux

### Terminal flow (`OpenForecourt.Emv/Terminal`)
Implement as an explicit state machine:
1. Candidate list building from PPSE, application selection by priority
2. Initiate application processing (GPO)
3. Read application data via AFL
4. Processing restrictions (expiry, AUC)
5. Terminal risk management — floor limit check, random transaction selection
6. Cardholder verification — online PIN, signature, no CVM, from the CVM list
7. Terminal action analysis against TAC/IAC denial and online bits
8. First `GENERATE AC` requesting ARQC
9. Build EMV field 55 for the ISO 8583 message from the required tags

## Tests
- TLV round trip on golden files plus malformed-input cases
- Full APDU trace golden test: run a transaction against the virtual card in-proc and
  assert the exact command/response sequence
- Terminal flow branch tests: below floor limit, above floor limit, expired card,
  each CVM path
- The PC/SC adapter tests are Windows-only and must be attributed so CI skips them
  cleanly on Linux

## Demo — `scripts/demo-02.ps1`
Run a full transaction against the virtual card and print the complete APDU trace with
tags decoded by name, then print the assembled field 55.

## Notes
Where an exact EMV specification detail is not certain, mark it `// SPEC-UNVERIFIED:`
and list it in the walkthrough. Honest gaps are fine. Confident fabrication is not.
