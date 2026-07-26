# OpenForecourt

A fully simulated Electronic Payment System (EPS) for a fuel-station forecourt, modelling
the real architecture used by fuel-dispenser payment vendors: an outdoor payment terminal
(OPT) reading cards, a pump controller running firmware, a site controller orchestrating
transactions, and an acquiring host reached over TCP.

**Everything is simulated. There is no physical hardware** — by design. The simulators are
first-class, production-shaped components, not test mocks. The whole system is built on
ports and adapters: every external boundary is an interface with multiple real
implementations selected by configuration. Swapping a simulated card reader for a physical
ACR122U is a configuration change, not a code change.

> This is a portfolio project. It uses **test card numbers and test keys only** — never
> real cardholder data. See `CLAUDE.md` section 7.

## Architecture at a glance

```
ICardReader   -> Pcsc (winscard) | VirtualPcd (TCP) | InProc (CI)
IPumpTransport-> Serial (com0com) | Tcp (firmware host) | InProc (CI)
IHostConnection-> Tcp (ISO 8583)  | InProc (CI)
ISecureKeyStore, ITransactionJournal (SQLite WAL), IClock, ITokenVault
```

`OpenForecourt.Abstractions` holds every port and the domain value types and has **zero
dependencies** — enforced by a test. See `docs/adr/` for the decisions and
`docs/walkthroughs/` for line-by-line explanations of each phase.

## Build and test

Requires the **.NET 10 SDK**.

```bash
# Linux / CI — uses the solution filter that excludes the Windows-only adapters
dotnet test OpenForecourt.CI.slnf

# Windows dev — full solution including the PC/SC and serial adapters
dotnet test OpenForecourt.sln

# Operator console (Node 24)
cd web/forecourt-dashboard && npm ci && npm test
```

The quickest way to see the whole thing running is `./scripts/demo-06.sh`, which brings the
forecourt up under Docker Compose and puts the operator console on `http://localhost:8080`.

## Phases

### Phase 0 — Scaffold and ports ✅

A buildable, CI-green skeleton with every architectural seam defined and zero business
logic.

- Solution layout per `CLAUDE.md` section 4; `Directory.Build.props` with nullable +
  warnings-as-errors + `latest-recommended` analysis; `.editorconfig`; `.gitignore`.
- All port interfaces in `OpenForecourt.Abstractions/Ports/` with documented failure
  semantics: `ICardReader`, `IPumpTransport`, `IHostConnection`, `ISecureKeyStore`,
  `ITransactionJournal`, `IClock`, `ITokenVault`.
- Domain value types in `OpenForecourt.Abstractions/Domain/`: `Money` (integer minor
  units), `Volume` (millilitres), `Pan` (masked by default, greppable `Reveal()`),
  `PumpState`, `TransactionContext`.
- Tests: an architecture test asserting Abstractions has no infrastructure dependencies,
  and a masking test asserting `Pan.ToString()` never exposes more than the first 6 and
  last 4 digits.
- GitHub Actions CI building and testing on `ubuntu-latest` via the CI solution filter
  (Windows adapters excluded).

Run the demo:

```bash
./scripts/demo-00.sh        # Linux/macOS
pwsh ./scripts/demo-00.ps1  # Windows
```

ADRs: [0001 ports and adapters](docs/adr/0001-ports-and-adapters.md),
[0002 .NET 10 LTS](docs/adr/0002-dotnet-10-lts.md),
[0003 SQLite WAL journal](docs/adr/0003-sqlite-wal-journal.md).
Walkthrough: [00 — scaffold and ports](docs/walkthroughs/00-scaffold.md).

### Phase 1 — ISO 8583 codec and acquiring host simulator ✅

A correct, well-tested ISO 8583 message codec and an async TCP acquirer that authorises,
declines, times out, detects duplicates and reverses.

- **Data-driven codec** (`OpenForecourt.Iso8583`) over a documented dialect, **OFC-87**
  ([docs/protocol-iso8583.md](docs/protocol-iso8583.md)). A field table drives all
  encode/decode — no per-field parsing — so a different acquirer is a different table, not
  different code. Primary/secondary 8-byte bitmaps, Fixed/LLVAR/LLLVAR, ASCII numeric,
  alphanumeric and binary (field 55). Decoding is total: malformed input returns a
  diagnostic `Result`, never an exception.
- `Iso8583Message` is immutable after build, indexer + `TryGetField`, and a field-by-field
  `ToString()` trace **with the PAN masked**. Every point where the 1987 spec text was not
  verified is marked `SPEC-UNVERIFIED` in code and document.
- **Framing** — 2-byte big-endian length prefix over `PipeReader`, correct under
  byte-at-a-time and coalesced reads, with oversized/zero/truncated-length rejection.
- **Host simulator** (`OpenForecourt.HostSimulator`) — JSON-configured rule engine (approve
  below a threshold, decline by PAN, inject latency, inject silence), duplicate detection on
  terminal + STAN, and an in-memory ledger so reversals are verifiable. Decision, transport
  and ledger are separated so timing behaviour is unit-testable without waiting.
- Tests: seeded property-based round trip, six byte-exact human-annotated golden files,
  bitmap edge cases, framing edge cases, engine decisions, an end-to-end TCP concurrency
  test (N terminals racing a shared STAN → exactly one approval), and a PAN-leak test that
  captures every trace sink.

Run the demo (starts the host, sends echo/approval/duplicate/declines/timeout/reversal, and
prints the decoded trace of every message both directions):

```bash
./scripts/demo-01.sh          # Linux/macOS
pwsh ./scripts/demo-01.ps1    # Windows
```

ADRs: [0004 data-driven codec](docs/adr/0004-iso8583-data-driven-codec.md),
[0005 host decision vs transport](docs/adr/0005-host-simulator-decision-vs-transport.md).
Walkthrough: [01 — ISO 8583](docs/walkthroughs/01-iso8583.md).
Protocol: [OFC-87 dialect](docs/protocol-iso8583.md).

### Phase 2 — Card layer: BER-TLV, virtual card, PC/SC, EMV online flow ✅

A terminal that runs a real EMV online-authorisation flow against a card we also wrote, over
the same `ICardReader` boundary the physical Windows PC/SC reader implements.

- **BER-TLV codec** (`OpenForecourt.Emv/BerTlv`) — multi-byte tags packed into an integer
  losslessly, constructed/primitive tree, **byte-identical** re-serialisation, and a tag
  dictionary so traces read as names. Parsing is total: truncated tags, over-long lengths, the
  indefinite form and adversarially deep nesting all return a diagnostic `Result`, never an
  exception or a loop.
- **Virtual card** (`OpenForecourt.VirtualCard`) — the card side of the conversation, an APDU
  responder driven by JSON profiles under `tests/testdata/cards/`. Handles SELECT (PPSE/PSE and
  by AID with a 6F/A5 FCI), GET PROCESSING OPTIONS (AIP + AFL), READ RECORD via the AFL, GET
  DATA (ATC, last online ATC), and GENERATE AC (ARQC), with correct `9000 / 6A82 / 6A86 / 6700
  / 6985 / 6A83 / 6A88` status words. Three profiles: contact chip, contactless, and an
  offline-decline (returns an AAC). The card builds real TLV with the production writer, so an
  in-process transaction exercises the genuine message flow.
- **Card-reader adapters** — `PcscCardReader` is **direct `winscard.dll` P/Invoke** (no wrapper),
  the real Windows production path (excluded from Linux CI); `InProcCardReader` calls straight
  into the virtual card and is what CI runs the whole flow against. (A third `vsmartcard` adapter
  is backlogged — see ADR 0008.)
- **Terminal kernel** (`OpenForecourt.Emv/Terminal`) — the EMV online subset as an explicit state
  machine: candidate list, application selection, GPO, read application data, processing
  restrictions (expiry, effective date, AUC), terminal risk management (floor limit, random
  selection), cardholder verification (online PIN / signature / no CVM from the CVM list),
  terminal action analysis against the action codes, first GENERATE AC requesting an ARQC, and
  assembly of **ISO 8583 field 55**. Every card exchange is recorded for the trace.
- Tests: golden BER-TLV round trip, malformed/fuzz cases, card-level status-word behaviour, a
  full **golden APDU trace** of an in-process transaction, terminal branch tests (below/above
  floor limit, expired card, each CVM path, offline decline), field-55 content, and PAN masking
  in the decoded trace. PC/SC adapter tests are Windows-only and excluded from CI.

Run the demo (a full transaction against each of the three cards, printing the complete APDU
trace decoded by tag name and the assembled field 55):

```bash
./scripts/demo-02.sh          # Linux/macOS
pwsh ./scripts/demo-02.ps1    # Windows
```

ADRs: [0006 BER-TLV codec](docs/adr/0006-ber-tlv-codec.md),
[0007 data-driven virtual card](docs/adr/0007-virtual-card-data-driven-profiles.md),
[0008 direct winscard P/Invoke](docs/adr/0008-direct-winscard-pinvoke.md),
[0009 EMV terminal state machine](docs/adr/0009-emv-terminal-state-machine.md).
Walkthrough: [02 — Card layer](docs/walkthroughs/02-card-layer.md).

### Phase 3 — DUKPT, PIN blocks, tokenization, threat model ✅

The security substrate that makes the P2PE and PIN-security stories architecturally true, plus
a written threat model. All of it against test keys and test PANs only (CLAUDE.md §7).

- **DUKPT, TDES** (`OpenForecourt.Crypto/Dukpt`) — ANSI X9.24-1: BDK→IPEK derivation, the `Ksn`
  value type (initial key serial number + 21-bit counter, advancing and refusing past
  exhaustion), the non-reversible future-key derivation, and per-transaction PIN and data key
  variants. The BDK→IPEK step is asserted against the **published X9.24 vector**
  (`FFFF9876543210E00000` → `6AC292FAA1315B4D858AB3A3D7D5933A`); advancement and variants are
  proven by round-trip (terminal derives from the IPEK and encrypts; host re-derives from the BDK
  and decrypts to the same plaintext). Genuinely unsourced details carry `SPEC-UNVERIFIED`
  (CLAUDE.md §11).
- **PIN blocks** (`OpenForecourt.Crypto/Pin`) — ISO 9564 Format 0 (PAN-XOR, TDES) and Format 4
  (AES), encode/encrypt/decrypt round-tripped, with the Format 0 clear block pinned to a known
  vector. PINs and PIN blocks are never logged at any level, enforced by the PAN-scan test.
- **Tokenization** (`OpenForecourt.Crypto/Tokenization`) — `FpeTokenVault`, the `ITokenVault` at
  the OPT boundary: a deterministic, format-preserving, Luhn-valid surrogate that preserves the
  last four and reveals nothing (HMAC-derived middle, adjust one digit for Luhn). Nothing
  downstream of the OPT ever holds a cleartext PAN; a test walks every artefact and sink to prove
  it.
- **Key store** (`OpenForecourt.Crypto/KeyStore`) — `DpapiKeyStore` (Windows DPAPI at-rest,
  `[SupportedOSPlatform("windows")]`, excluded from Linux CI) and `InMemoryKeyStore` (tests only,
  **throws at construction in a Release build**).
- **Threat model** ([docs/threat-model.md](docs/threat-model.md)) — STRIDE over the four trust
  boundaries, the Secure Cryptographic Device boundary, the key-management lifecycle, a PCI DSS
  scope map, and an honest "what this simulation does not prove".
- Tests: DUKPT IPEK vector + counter advances + terminal/host round trip, PIN block round trip
  (both formats), token determinism/Luhn/irreversibility, the log-and-storage PAN scan, and the
  Release key-store guard.

Run the demo (derive the IPEK, advance the KSN across three transactions with a different derived
key each, encrypt and decrypt a PIN block, and print a record with the token in place of the PAN):

```bash
./scripts/demo-03.sh          # Linux/macOS
pwsh ./scripts/demo-03.ps1    # Windows
```

ADRs: [0010 DUKPT verification strategy](docs/adr/0010-dukpt-tdes-verification-strategy.md),
[0011 format-preserving tokenization](docs/adr/0011-format-preserving-tokenization.md),
[0012 key-store split](docs/adr/0012-key-store-split-dpapi-vs-inmemory.md).
Walkthrough: [03 — Crypto and keys](docs/walkthroughs/03-crypto-keys.md).

### Phase 4 — Pump controller firmware and transport adapters ✅

Real embedded C firmware for the dispenser, dual-targeted so the identical source runs on a
desktop host and would compile for an STM32, driving a state machine the C# pump manager talks to
over three interchangeable transports. The wire protocol is specified first, in
[docs/protocol-pump.md](docs/protocol-pump.md) (**OFP-1**, an original protocol inspired by IFSF —
written and committed before any pump code).

- **Firmware** (`firmware/pump/`) — a portable C core (`src/`) behind a six-function HAL
  (`hal/hal.h`): CRC-16/CCITT-FALSE, a byte-stuffed frame codec with a streaming decoder, the
  dispenser FSM as an **explicit transition table**, a non-resettable totalizer, flow-meter and
  preset handling, watchdog and meter-stall fault latching, and idempotent duplicate detection so a
  lost AUTHORISE ACK never double-authorises. **No dynamic allocation, no `printf`, no blocking.**
  Two HAL builds selected by a CMake `HAL` option: `host/` (UART → TCP socket, run as a normal
  executable) and `target/` (STM32Cube HAL, cross-compiled by CI under `arm-none-eabi-gcc`,
  archived compile-only — the portability proof, never run here).
- **Pump manager** (`OpenForecourt.PumpManager`) — one actor per pump (`PumpSession`) with one
  inbound-frame channel and one outbound-command channel drained by a single loop, so **no locks**
  guard pump state; up to 8 pumps concurrently (`PumpManagerService`). Command/response correlation
  by sequence number with `IClock`-measured timeout and retransmit, reconnect with exponential
  backoff + jitter, and a **mirrored C# copy of the dispenser FSM** that flags illegal transitions
  the firmware reports as protocol violations. The OFP-1 wire codec is the C# twin of the firmware
  codec.
- **Transports (one `IPumpTransport` port)** — `TcpPumpTransport` (firmware host build),
  `InProcPumpTransport` (codec-free in-memory pipe, CI), `SerialPumpTransport` (com0com null-modem,
  Windows-only, shares the codec). Swapping one for another is configuration, not code.
- Tests: the C tests (assert harness) cover the CRC check value, the golden AUTHORISE frame (15
  stuffed bytes), stuffing/CRC/resync edges, and every legal + illegal FSM cell; the C# tests
  assert the **same golden frame byte-for-byte** (cross-language parity), the FSM table matches
  cell-for-cell, 8 concurrent pumps under load with no cross-talk and clean shutdown, and
  deterministic timeout/retry on a self-advancing fake clock.

Run the demo (build the firmware, launch four host processes, fuel two pumps concurrently with a
live frame trace, drop a nozzle mid-dispense, and kill + restart a pump to show timeout and
recovery):

```bash
./scripts/demo-04.sh          # Linux/macOS/WSL2
pwsh ./scripts/demo-04.ps1    # Windows
```

ADRs: [0013 OFP-1 pump protocol](docs/adr/0013-ofp1-original-pump-protocol.md),
[0014 dual-target firmware HAL](docs/adr/0014-dual-target-firmware-hal.md),
[0015 pump-manager actor and transports](docs/adr/0015-pump-manager-actor-and-transports.md).
Walkthrough: [04 — Pump firmware](docs/walkthroughs/04-pump-firmware.md).

### Phase 5 — Site controller: REST, SignalR, RabbitMQ, crash-safe journal ✅

The orchestration tier, and the phase that is really about **failure paths**. `OpenForecourt.SiteController`
is an ASP.NET Core host that ties the ports together: a crash-safe SQLite journal, a TCP link to the
acquiring host, RabbitMQ store-and-forward, a SignalR feed, and offline authorisation that keeps the
forecourt selling fuel when the host is unreachable.

- **Crash-safe journal (`SqliteJournal`, WAL).** The intent is journalled **before** the host request
  is sent, and every lifecycle step (`Intent → HostRequestSent → Approved → Completed`, with
  `Declined`/`Reversed`/`Voided` branches) is durable before its side effect. On startup,
  `RecoveryService` reconciles every in-flight transaction: void an intent that never reached the
  host, **reverse** a request whose host state is unknown, resume settlement, or re-queue an offline
  approval. A test kills (simulates) the process at each of five defined points and asserts recovery.
- **Offline mode.** When `HostProber` marks the host down, authorisations under a configurable **floor
  limit** are approved offline within a total **exposure ceiling** (`OfflinePolicy`) and queued durably
  on the journal. On reconnection they **replay in order, exactly once** (the host dedupes on
  transaction id and each record is driven terminal); a declined replay generates a reversal/exception.
- **Dispatch (`ITransactionDispatch`).** `RabbitMqDispatch` — durable `settlement` queue + durable
  `settlement.dlq` dead-letter queue, persistent messages, redelivery-then-dead-letter — with an
  `InProcDispatch` twin for CI. The `SettlementConsumer` is **idempotent by transaction id**.
- **REST API** (`/api/v1/…`: pumps, authorise, cancel, transactions, totals, health) with RFC 7807
  problem details, **idempotency keys** on state-changing endpoints, a committed OpenAPI document, a
  correlation id flowing OPT→host, OpenTelemetry traces, and **Serilog PAN masking enforced at the
  sink** so no log call can leak a PAN.
- **SignalR hub** with **per-connection drop-oldest backpressure** (`PerConnectionDispatcher`) so a
  slow dashboard never stalls the pump pipeline, and snapshot-based resynchronisation on reconnect.
- **Ports as adapters:** `SqliteJournal`/`InMemoryJournal`, `RabbitMqDispatch`/`InProcDispatch`,
  `TcpHostConnection`/`InProcHostConnection` — CI runs the full failure-path suite on Linux with no
  broker and no host; the demo runs the real thing under Docker Compose.

Run the demo (`docker compose up` the forecourt, authorise four pumps, kill the host and watch
offline authorisation continue, restart the site controller and watch recovery, restore the host and
watch replay + reconciliation):

```bash
./scripts/demo-05.sh          # Linux/macOS/WSL2 (Docker required)
pwsh ./scripts/demo-05.ps1    # Windows
```

ADRs: [0016 write-intent-before-send recovery](docs/adr/0016-write-intent-before-send-recovery.md),
[0017 dispatch port over RabbitMQ](docs/adr/0017-transaction-dispatch-port-rabbitmq.md),
[0018 offline authorisation & orchestration scope](docs/adr/0018-offline-authorisation-and-orchestration-scope.md),
[0019 SignalR backpressure & PAN masking](docs/adr/0019-signalr-per-connection-backpressure.md).
Walkthrough: [05 — Site controller](docs/walkthroughs/05-site-controller.md).

### Phase 6 — Operator console, forecourt simulator, fault injection ✅

The visible surface, and the phase where every previous one comes together on a single path. The
site controller now also hosts the outdoor payment terminal, launches one **pump-firmware process
per dispenser**, and serves an Angular operator console at `http://localhost:8080`.

- **Forecourt dashboard.** An eight-pump live grid — state, metered volume, metered value, grade
  and unit price, card brand, session elapsed, OFP-1 link state — plus a filterable transaction
  feed, site totals and an end-of-day reconciliation readout. Angular 21, standalone components,
  **signals only**, zoneless change detection, strict templates, no `any`.
- **Transaction detail drawer — the full trace.** The literal APDU exchange with the card
  (command/response hex, response TLV decoded by tag name), the ISO 8583 request and response
  **decoded field by field**, the OFP-1 commands and events, and a **timing waterfall** over all
  of it. Every step was captured by the component that did the work; nothing is reconstructed.
  Sensitive fields are masked by the dialect table's own `Sensitive` flag, not by the view.
- **The OPT runs in-process.** Presenting a card runs the real EMV kernel against the Phase 2
  virtual card profiles. The cleartext PAN exists only inside one method, is tokenized there, and
  never reaches the orchestrator, the journal or the queue — the P2PE boundary is now a method
  scope, asserted by a test. Online PIN is driven by the card's CVM list, captured on an on-screen
  keypad with a visible timeout, and encrypted under a DUKPT-derived key; the PIN block is never
  logged, stored or traced.
- **Real dispensers.** `PumpFleet` drives one `pump_host` process per pump over OFP-1 — the same
  portable C core Phase 4 cross-compiles for a Cortex-M4. The volume and value on the grid are
  metered by firmware. The firmware's host HAL gained a stdin control channel so the operator
  panel's nozzle, flow-rate and grade controls land on the same entry points a sensor ISR calls.
- **Pre-authorisation and settlement on delivery.** With dispensers on the path an approval is a
  pre-auth; the transaction settles for what the meter actually delivered, and a crash mid-fuelling
  is resumed by the existing recovery path.
- **Fault-injection console — nothing is simulated.** Each control arms a real fault on a real
  path: severing the acquirer link drives the site into offline authorisation; forcing a response
  code sends the test PAN the **host** declines; corrupting a frame CRC damages the framed bytes so
  the **firmware** rejects them and the manager retransmits; a power cut kills the firmware
  process; suspending mid-authorisation parks the system between the journal write and the wire;
  a forced duplicate is caught by the **acquirer's** STAN ledger.
- **OPT media loop.** An advertising video on the terminal idle screen that pauses for the duration
  of a transaction and resumes afterwards, driven by terminal state rather than by a click handler.
- **Tests.** Pump-tile rendering across every `PumpState`; SignalR reconnection and full-snapshot
  resynchronisation against a stub hub; one Playwright end-to-end fuelling driven through the
  browser against the real stack, firmware included.

Run the demo (the browser is the demo — `docker compose up` the whole forecourt, then a scripted
fuelling and two injected faults over the same API the console uses):

```bash
./scripts/demo-06.sh          # Linux/macOS/WSL2 (Docker required)
pwsh ./scripts/demo-06.ps1    # Windows
```

Front-end development against a running site controller:

```bash
cd web/forecourt-dashboard
npm ci
npm start        # http://localhost:4200, proxying /api and /hubs to :8080
npm test         # unit tests
npm run e2e      # Playwright; needs firmware/pump/build-host/pump_host
```

ADRs: [0020 OPT inside the site controller](docs/adr/0020-opt-inside-the-site-controller.md),
[0021 firmware pumps on the orchestration path](docs/adr/0021-firmware-pumps-on-the-orchestration-path.md),
[0022 pre-authorisation & settlement on delivery](docs/adr/0022-preauthorisation-and-settlement-on-delivery.md),
[0023 faults armed, not simulated](docs/adr/0023-faults-armed-not-simulated.md),
[0024 Angular signals & snapshot resync](docs/adr/0024-angular-signals-and-snapshot-resync.md).
Walkthrough: [06 — Operator console](docs/walkthroughs/06-frontend.md).
