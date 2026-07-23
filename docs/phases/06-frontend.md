# Phase 6 — Angular Dashboard, Forecourt Simulator, Fault Injection

**Goal:** The visible surface. This is what a recruiter sees in 30 seconds and what
makes the demo video work.

## In scope
`web/forecourt-dashboard`

## Deliverables

### Forecourt dashboard
- 8-pump live grid: state, current volume, current value, card brand, elapsed time
- Real-time via SignalR with signals-based state, automatic reconnect and resync
- Transaction feed with a live filter
- Transaction detail drawer showing the **full trace**: APDU exchange, ISO 8583
  request and response field by field, timing waterfall. This is the screen that
  demonstrates depth.
- Site totals and end-of-day view
- No `any`, standalone components, strict templates

### Forecourt simulator panel
The operator side, driving the simulated hardware:
- Nozzle lift and replace per pump
- Preset selection (value or volume)
- Grade selection
- Manual flow rate control

### Card simulator
- Pick a test card profile, choose contact / contactless / magstripe fallback
- PIN entry pad for online PIN flows
- Visible countdown for terminal timeouts

### Fault injection console
This is the differentiating feature. Each control triggers a real fault in the running
system, not a UI mock:
- Drop nozzle mid-dispense
- Kill the host link, or add configurable latency
- Force a specific host response code
- Corrupt a pump frame CRC
- Power-cut a pump mid-transaction
- Suspend the site controller mid-authorisation
- Force a duplicate transaction

Every injected fault must produce a correct, observable system response — that is the
point of the feature.

### OPT media loop
An advertising video (MP4) playing on the terminal idle screen, pausing on transaction
start and resuming on completion. This covers the video/audio requirement with minimal
effort — do not over-invest here.

## Tests
- Component tests for pump tile state rendering across all `PumpState` values
- SignalR client reconnection and resync test against a stub hub
- One Playwright end-to-end test: complete a fuelling transaction through the UI

## Demo — `scripts/demo-06.ps1`
Bring up the full stack plus the front end and print the URL. The demo is the browser.

## Design note
Read the frontend design guidance before styling. Aim for an operator console
aesthetic — dense, legible, high contrast, monospace for traces and hex. Not a
consumer dashboard.
