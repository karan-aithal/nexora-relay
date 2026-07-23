# CLAUDE.md — OpenForecourt

This file is persistent context. Read it in full at the start of every session, before
reading any phase file.

---

## 1. What this project is

**OpenForecourt** is a fully simulated Electronic Payment System (EPS) for a fuel
station forecourt. It models the real architecture used by fuel dispenser payment
vendors: an outdoor payment terminal (OPT) reading cards, a pump controller running
firmware, a site controller orchestrating transactions, and an acquiring host reached
over TCP.

**Everything is simulated. There is no physical hardware.** This is a deliberate
design constraint, not a limitation to be worked around. The simulators are
first-class production-shaped components, not test mocks.

### Why it exists

This is a portfolio project targeting a Senior Engineer (C# / embedded payments) role
at a fuel-dispenser manufacturer. It must demonstrate, with running code:

- C# / .NET Core, strong OOP, SOLID, real multi-threading
- REST and RESTful web APIs, SignalR, RabbitMQ, Docker
- Angular front end
- Real-time embedded development in C, with hardware abstraction
- TCP/IP sockets, serial communication, USB (PC/SC), video playback
- EMV, P2PE and PCI concepts applied, not just name-dropped
- Comfort building and working with hardware simulators

Optimise for **demonstrable depth in payments and concurrency**. Do not optimise for
feature count.

---

## 2. Environment

- **Host OS:** Windows 11 with WSL2 available
- **Editor:** VS Code / JetBrains with Claude Code
- **.NET 10 (LTS)** — C# 14, `net10.0`
- **Node LTS + Angular** (latest stable) for the web front end
- **CMake + GCC/Clang** for firmware host builds (WSL2 is fine for this)
- **Docker Desktop** with WSL2 backend

### Platform split — important

| Concern | Runs where | Why |
|---|---|---|
| PC/SC card reader adapter | **Windows native** | `winscard.dll` P/Invoke — this is the real production path |
| Serial port pair | **Windows native** | `com0com` virtual null-modem |
| Firmware host build | **WSL2** | GCC toolchain, cppcheck |
| Site controller, host sim, RabbitMQ | **Docker** | cross-platform |

Never assume a single platform. Anything platform-specific goes behind a port
interface with a documented fallback, and CI must be able to run the full test suite
on Linux without the Windows-only adapters.

---

## 3. Architecture — ports and adapters

The entire design rests on this. **Every external boundary is an interface with
multiple real implementations, selected by configuration.** Simulation is one
implementation among several, never a special case in the business logic.

```
ICardReader        -> PcscCardReader        (winscard.dll P/Invoke, Windows)
                   -> VirtualPcdCardReader  (vsmartcard vpcd over TCP)
                   -> InProcCardReader      (direct call into VirtualCard, for CI)

IPumpTransport     -> SerialPumpTransport   (COM port via com0com)
                   -> TcpPumpTransport      (firmware host-build over socket)
                   -> InProcPumpTransport   (in-memory frame pipe, for CI)

IHostConnection    -> TcpHostConnection     (length-prefixed ISO 8583 over TCP)
                   -> InProcHostConnection  (for CI)

ISecureKeyStore    -> DpapiKeyStore         (Windows DPAPI)
                   -> InMemoryKeyStore      (tests only, refuses to run in Release)

ITransactionJournal-> SqliteJournal         (WAL mode, crash-safe)
                   -> InMemoryJournal       (tests only)

IClock             -> SystemClock / FakeClock   (all timeouts must be testable)
```

**Rule: mock at the port, never above it.** If a test needs to fake an
`ITransactionService`, the seam is in the wrong place — fix the design instead.

The payoff, and the thing to state plainly in the README: swapping a simulated card
reader for a physical ACR122U is a configuration change, not a code change.

---

## 4. Repository layout

```
openforecourt/
├── CLAUDE.md
├── README.md
├── OpenForecourt.sln
├── Directory.Build.props
├── .editorconfig
├── docs/
│   ├── phases/              00..07 — the executable phase specs
│   ├── adr/                 one ADR per significant decision
│   ├── walkthroughs/        one per phase — see section 8
│   ├── protocol-pump.md     written in Phase 4 BEFORE any pump code
│   └── threat-model.md      written in Phase 3
├── src/
│   ├── OpenForecourt.Abstractions/     ports + domain model, zero dependencies
│   ├── OpenForecourt.Iso8583/          message codec
│   ├── OpenForecourt.Emv/              BER-TLV + terminal kernel subset
│   ├── OpenForecourt.Crypto/           DUKPT, PIN blocks, tokenization
│   ├── OpenForecourt.Adapters.Pcsc/    Windows-only
│   ├── OpenForecourt.Adapters.Serial/  Windows-only
│   ├── OpenForecourt.Adapters.InProc/
│   ├── OpenForecourt.VirtualCard/      card-side APDU responder
│   ├── OpenForecourt.Opt/              outdoor payment terminal application
│   ├── OpenForecourt.PumpManager/      pump session orchestration
│   ├── OpenForecourt.SiteController/   ASP.NET Core host
│   └── OpenForecourt.HostSimulator/    acquirer simulator
├── firmware/pump/
│   ├── src/                 portable C — dispenser FSM, totalizer, framing
│   ├── hal/hal.h            the abstraction
│   ├── hal/host/            host build: UART -> TCP socket, timer -> host clock
│   ├── hal/target/          STM32 stubs, compiled but never run here
│   └── CMakeLists.txt
├── web/forecourt-dashboard/ Angular
├── tests/
│   ├── *.UnitTests/
│   └── OpenForecourt.E2ETests/
├── deploy/docker-compose.yml
└── scripts/                 demo-00.ps1 ... demo-07.ps1 (+ .sh equivalents)
```

---

## 5. Locked technical decisions

Do not revisit these without writing an ADR that argues the change.

- **.NET 10 LTS.** Chosen deliberately — industrial payment estates run conservative,
  long-support stacks.
- **SQLite in WAL mode** for the transaction journal. This is device-appropriate: it
  must survive power loss mid-transaction and replay on restart. No PostgreSQL.
- **Angular** for both the dashboard and the forecourt simulator panel. One front end,
  not two. No WPF, no second SPA framework.
- **RabbitMQ** for transaction dispatch and store-and-forward.
- **Docker Compose only.** No Kubernetes, no service mesh, no gRPC.
- **EMV scope: online authorisation (ARQC) only** through Phase 7. Offline data
  authentication (SDA/DDA/CDA) is optional Phase 8 and must not be attempted early.
- **`System.Threading.Channels`** is the default concurrency primitive for pipelines.
  TPL Dataflow only with justification.

---

## 6. Coding standards

### C#
- `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`,
  `<AnalysisLevel>latest-recommended</AnalysisLevel>` in `Directory.Build.props`
- File-scoped namespaces, primary constructors where they read well
- **No `async void`.** No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()`
- `CancellationToken` threaded through every async path, including private methods
- Every timeout comes from configuration and uses `IClock` — never `Task.Delay` against
  the real clock inside testable logic
- Prefer `readonly record struct` for value types (`Money`, `Volume`, `Ksn`, `Pan`)
- No exceptions for expected outcomes. A declined transaction is a return value.

### C (firmware)
- C11, `-Wall -Wextra -Werror -pedantic`
- **No dynamic allocation after init.** No `malloc` in the run loop.
- No `printf` in code compiled for the target HAL — logging goes through `hal_log()`
- All state machines are explicit tables or switch statements, never implicit flags
- `cppcheck` clean in CI

### Angular
- Standalone components, signals for state, strict template checking
- No `any`

---

## 7. Security and compliance rules — non-negotiable

This repository is public. Treat these as hard constraints.

1. **Never use real cardholder data.** Test PANs only, from the reserved test ranges
   (`4111111111111111`, `5555555555554444`, `378282246310005`, etc.).
2. **All keys are test keys.** BDKs, IPEKs and CA keys live under `tests/testdata/`
   with a `TEST-ONLY-DO-NOT-USE.md` alongside. Never in `src/`, never in config
   defaults, never in a Dockerfile.
3. **PAN masking is enforced by a test**, not by discipline. Write a test that runs a
   full transaction, captures every log sink, and fails if a PAN-shaped digit sequence
   appears unmasked. Masking format: first 6 + last 4.
4. **Cleartext PAN never leaves the OPT boundary.** Downstream components see a token.
   This is the P2PE story and it must be architecturally true, not just documented.
5. **PIN blocks are never logged at any level, ever.** Not even at Trace.
6. `.gitignore` must cover `*.pfx`, `*.key`, `appsettings.*.Local.json`, `.env`.

---

## 8. Documentation obligations

This project was built with heavy AI generation. The reviewer's credibility depends on
being able to explain every line of it. Therefore:

**Every phase must produce `docs/walkthroughs/NN-<name>.md` containing:**

1. **Design rationale** — what was built and why it is shaped that way
2. **The three hardest parts**, explained line by line, as if teaching someone who has
   never seen the domain. Be specific about the code, not general about the concept.
3. **What was rejected and why** — alternatives considered
4. **Self-quiz** — 5 questions the author should be able to answer without notes. Make
   them the questions an interviewer would actually ask. Do not answer them.
5. **Known weaknesses** — what is simplified relative to a production system

This is a deliverable, not a nicety. A phase without its walkthrough is incomplete.

**ADRs** (`docs/adr/NNNN-title.md`) use the standard form: Context, Decision,
Consequences, Alternatives Considered. One per significant decision, numbered
sequentially.

---

## 9. Definition of Done — every phase

A phase is complete only when **all** of these hold:

1. `dotnet build` succeeds with **zero warnings**
2. Firmware host build succeeds, `cppcheck` clean (from Phase 4 onward)
3. All tests pass; new algorithmic code has meaningful unit tests, not smoke tests
4. `scripts/demo-NN.ps1` runs unattended and prints observable, meaningful output
   proving the phase works
5. `docs/adr/` has an entry for each significant decision made
6. `docs/walkthroughs/NN-*.md` is written per section 8
7. `README.md` section for this phase is updated
8. Changes committed using Conventional Commits (`feat:`, `test:`, `docs:`, `refactor:`)
9. CI is green

Then **stop.** Report what was built, what the demo shows, and what is open. Do not
begin the next phase in the same session.

---

## 10. How to work

1. Read this file. Read the requested phase file.
2. **Plan first.** Produce a short implementation plan and list any ambiguity in the
   spec. If a spec detail is genuinely underdetermined, ask rather than guess.
3. Implement in small, committed increments. Tests alongside the code, not after.
4. Run the demo script yourself and paste the real output — never describe output you
   have not observed.
5. Stop at the Definition of Done.

## 11. Never do these

- **Never invent EMV tags, ISO 8583 field definitions, or DUKPT steps from memory.**
  If the exact specification detail is not certain, say so explicitly and mark it
  `// SPEC-UNVERIFIED:` with a note in the walkthrough. A confidently wrong bitmap
  parser is worse than an honest gap.
- Never write pump code before `docs/protocol-pump.md` exists and is complete.
- Never add a dependency without justifying it in the commit message.
- Never expand scope beyond the current phase file. Note the idea in
  `docs/backlog.md` and move on.
- Never claim a test passes without running it.
- Never use real cardholder data or real cryptographic keys.
- Never skip the walkthrough document.
