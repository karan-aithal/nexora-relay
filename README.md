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
```

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
