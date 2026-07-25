# OpenForecourt — Threat Model

This document is a deliverable of Phase 3 (CLAUDE.md §7, phase 03). It is a STRIDE analysis
over the four trust boundaries of the forecourt payment system, an account of the Secure
Cryptographic Device boundary, the key-management lifecycle, a PCI DSS mapping, and — most
importantly — an honest statement of what this simulation does **not** prove.

Everything here is written against the architecture as built. Where a control is simulated
rather than real, it says so.

---

## 1. Assets and trust boundaries

The things worth protecting, in order:

1. **Cleartext PAN** — the primary account number. Sensitive from the moment the card is read
   until it is tokenized at the OPT.
2. **PIN** — the cardholder's secret. Sensitive from entry until it is inside an encrypted PIN
   block; never recoverable outside a Secure Cryptographic Device in a real system.
3. **Key material** — the BDK (host side), the IPEK (terminal side), and every derived key.
4. **The transaction record** — amount, result, token, KSN. Integrity matters (it is the basis
   for settlement); confidentiality matters less once the PAN is a token.

The four boundaries the data crosses:

```
        card  ──①──▶  OPT  ──②──▶  site controller  ──③──▶  acquiring host
                                        │
                                        └──④──▶  operator dashboard
```

| # | Boundary | What crosses | The security story |
|---|---|---|---|
| ① | Card → OPT | APDUs: PAN, EMV data, ARQC | Physical/contactless read; PAN is cleartext **inside** the OPT only |
| ② | OPT → site controller | **Token**, masked PAN, encrypted PIN block, KSN, field 55 | P2PE: no cleartext PAN crosses here — the token does |
| ③ | Site controller → host | ISO 8583: token/PAN-in-field, amount, KSN, PIN block | Length-prefixed TCP; host re-derives the DUKPT key from the KSN |
| ④ | Operator → dashboard | Transaction views, pump control | Masked PAN and token only; never PIN, never cleartext PAN |

---

## 2. STRIDE per boundary

STRIDE = Spoofing, Tampering, Repudiation, Information disclosure, Denial of service,
Elevation of privilege. For each boundary: the threats that matter and how the design answers
them (or, honestly, does not).

### ① Card → OPT

- **Spoofing** — a counterfeit card. Answered in a real system by EMV offline/online card
  authentication (SDA/DDA/CDA) and the online ARQC the issuer verifies. This project does the
  **online ARQC** (Phase 2) but **not** offline data authentication — out of scope until the
  optional Phase 8. A cloned static-data card is therefore not detected offline here.
- **Tampering** — altered APDU responses. The ARQC is computed over transaction data, so a
  tampered amount fails issuer verification. The BER-TLV codec never trusts card input (it
  cannot throw, ADR 0006), so malformed TLV cannot crash the terminal.
- **Information disclosure** — the PAN is in the clear on this wire by necessity (the card
  presents it). This is exactly why the OPT is the P2PE boundary: cleartext PAN exists here and
  nowhere downstream.
- **DoS** — a stuck or hostile card stalls one pump; every card operation is cancellable via
  `CancellationToken` and bounded by an `IClock` timeout.
- **Elevation** — not applicable at the card interface; the card is a peripheral, not a caller.

### ② OPT → site controller

- **Information disclosure** — *the* boundary this whole project is built around. The OPT
  tokenizes the PAN (`FpeTokenVault`) and encrypts the PIN (`PinBlock` under a DUKPT key)
  **before** anything leaves. The site controller, journal, RabbitMQ payloads, dashboard and
  logs see only a token and a masked PAN. Enforced by a test that scans every artefact and sink
  (`PanScanTests`, `PanLeakTests`), not by discipline.
- **Tampering / Spoofing** — a real deployment mutually authenticates the OPT and controller
  (device certificates, a secure channel). This simulation runs them in-process or over local
  TCP and does **not** authenticate the channel — a stated gap (§6).
- **Repudiation** — the transaction journal (SQLite WAL, Phase 5) is the crash-safe record.
- **DoS / Elevation** — bounded queues and cancellation; no privilege crosses here.

### ③ Site controller → acquiring host

- **Tampering** — the ISO 8583 message carries the amount and the PIN block. A real link is
  under a MAC (message authentication code) keyed by a DUKPT MAC key; **this project derives PIN
  and data keys but not a MAC key**, and the host simulator does not verify a MAC — a stated
  gap. The PIN block itself is integrity-protected only insofar as a wrong key yields a garbage
  block the host rejects.
- **Information disclosure** — the PIN never travels except as an encrypted block; the host
  re-derives the same DUKPT key from the KSN (no key is transmitted). If a token is used in the
  PAN field, the host maps it; if a PAN is used, it is the acquirer's own boundary.
- **Spoofing** — a real link is TLS with mutual authentication to a known acquirer endpoint.
  The simulator uses plain TCP on loopback — a stated gap.
- **DoS** — length-prefixed framing with bounded reads; timeouts via `IClock`.

### ④ Operator → dashboard

- **Elevation / Spoofing** — pump control and transaction views need authenticated, authorized
  operators. Authn/authz on the dashboard is **not** implemented in this simulation — a stated
  gap; a real system puts role-based access in front of every control.
- **Information disclosure** — the dashboard is downstream of the token boundary, so it
  structurally cannot show a cleartext PAN, and never shows a PIN.
- **Repudiation** — operator actions would be audit-logged in a real system.

---

## 3. The Secure Cryptographic Device (SCD) boundary

A **Secure Cryptographic Device** — in payments, a PCI PTS-approved PIN Entry Device or an
HSM — is tamper-responsive hardware inside which PINs are entered and keys live. Its defining
property: **key material and cleartext PINs never exist outside it.** Keys are injected under
dual control, used inside the device, and zeroized on tamper. A cleartext PIN exists only
between the keypad and the encryption step, both inside the SCD.

Why it exists: it collapses the attack surface for the two highest-value assets (PIN, keys) to
one certified boundary, so the rest of the estate — controllers, hosts, networks — never
handles them in the clear and is therefore out of PCI PIN scope for those assets.

**In this project the SCD boundary is drawn but not physically enforced.** The OPT is the SCD:
it holds the IPEK, derives per-transaction keys, and produces PIN blocks. But it is a .NET
process, not tamper-responsive hardware — an attacker with host access could read the IPEK from
memory. `DpapiKeyStore` protects keys *at rest* under the OS user key; `InMemoryKeyStore`
refuses to run in Release; but neither is an HSM. This is the single most important gap and §6
states it plainly.

---

## 4. Key management lifecycle

| Stage | Real deployment | This simulation |
|---|---|---|
| **Generation** | BDK generated inside an HSM under dual control, never displayed | Published X9.24 **test** BDK in `tests/testdata/keys/` |
| **Injection** | IPEK derived from BDK + device KSN, injected into the PED at a secure facility under dual control, key-injection ceremony logged | `DukptTdes.DeriveIpek` called in-process; no ceremony, no dual control |
| **Storage** | HSM (host), tamper-responsive PED (terminal); keys never exported | `DpapiKeyStore` (at-rest, Windows) / `InMemoryKeyStore` (tests, blocked in Release) |
| **Use** | One unique key per transaction (DUKPT); PIN key, MAC key, data key variants | PIN and data variants implemented; MAC variant not |
| **Rotation** | KSN counter advances per transaction; device retired at counter exhaustion | `Ksn.Advance` advances; refuses past `MaxCounter` |
| **Compromise** | DUKPT forward security: a captured transaction key cannot derive earlier keys; a compromised device is deny-listed | Forward security holds (non-reversible derivation); no deny-list infrastructure |
| **Destruction** | Zeroization on tamper or decommission | `CryptographicOperations.ZeroMemory` on intermediate keys; no hardware zeroization |

The payoff of DUKPT, stated plainly: **no key is ever transmitted.** The host re-derives the
exact per-transaction key from the BDK and the KSN carried in the clear. Compromising one
transaction's key exposes that one transaction, not the device's history.

---

## 5. PCI DSS mapping

A mapping of the requirements this project *touches* to where it addresses them and what is out
of scope. This is not a compliance claim — it is a scope map.

| PCI DSS area | Where this project addresses it | Deliberately out of scope |
|---|---|---|
| Req 3 — protect stored account data | PAN tokenized at the OPT; only masked PAN + token persisted; masking is first-6/last-4, test-enforced | HSM-backed storage; key-encrypting-key hierarchy |
| Req 3.5 — protect keys | `DpapiKeyStore` at-rest protection; `InMemoryKeyStore` blocked in Release; keys only in `tests/testdata` | HSM key storage; dual control; split knowledge |
| Req 4 — encrypt transmission over open networks | DUKPT PIN encryption end-to-end; PIN never in clear on any wire | TLS/mutual-auth on the OPT↔controller and controller↔host links |
| Req 3.4 / P2PE — render PAN unreadable | The token boundary: no cleartext PAN downstream of the OPT | PCI P2PE-listed solution; certified PED |
| PIN security (PCI PTS / X9.24) | ISO 9564 Format 0/4 PIN blocks; DUKPT unique-key-per-transaction; PIN never logged (test-enforced) | Certified tamper-responsive PED; MAC on messages |
| Req 10 — log and monitor | PAN masking enforced across all sinks by test | Centralized SIEM; operator audit trail |
| Req 8 — authenticate access | — | Dashboard authn/authz (not implemented) |

---

## 6. What this simulation does **not** prove

Read this section as the counterweight to everything above. The architecture is real-shaped;
the assurances are not.

1. **No tamper-responsive hardware.** The OPT is a process. The IPEK and derived keys live in
   ordinary process memory and are readable by anyone with host access. A real PED's entire
   value is the physical boundary this cannot reproduce. This is the biggest gap.
2. **DUKPT derivation is partially vector-verified.** The BDK→IPEK step is checked against the
   canonical X9.24 vector; per-transaction key advancement and the PIN/data variants are proven
   by round-trip self-consistency, not against published per-counter vectors. The data-variant
   recipe and the Format 4 PIN block layout are marked `SPEC-UNVERIFIED` (CLAUDE.md §11).
3. **No channel authentication.** The OPT↔controller and controller↔host links are plain
   in-process/TCP. No TLS, no mutual auth, no message MAC. A real link has all three.
4. **No dashboard access control.** Authn/authz is not implemented.
5. **No key-injection ceremony.** Real IPEK injection is a dual-control physical process; here
   it is a function call.
6. **Test keys and test PANs only.** Nothing here has ever protected real cardholder data, and
   the repository is structured (CLAUDE.md §7) to make it impossible for it to.
7. **TDES, not AES-DUKPT.** The scheme is X9.24-1 (TDES). AES-DUKPT (X9.24-3) is the optional
   stretch and is not implemented; Format 4 uses a raw AES key rather than an AES-DUKPT key.

The purpose of this project is to demonstrate that the *architecture* — the P2PE token
boundary, unique-key-per-transaction, the SCD boundary, PAN masking enforced by test — is
understood and built correctly. It is explicitly not a certified or production-secure system.
