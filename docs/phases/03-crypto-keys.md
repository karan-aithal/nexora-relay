# Phase 3 — DUKPT, PIN Blocks, Tokenization, Threat Model

**Goal:** The security substrate, implemented against published test vectors, plus a
written threat model that shows you understand P2PE and PCI as an architecture, not a
checklist.

**Read first:** `CLAUDE.md` section 7 in full.

## In scope
`OpenForecourt.Crypto`, `docs/threat-model.md`

## Deliverables

### DUKPT (ANSI X9.24-1, TDES)
- BDK -> IPEK derivation from a Key Serial Number
- KSN structure: initial key serial number plus 21-bit transaction counter
- Future key register derivation, non-reversible key transformation
- Per-transaction key derivation, with PIN-encryption and data-encryption variants
- KSN advancement, and correct behaviour at counter exhaustion

**Validate against the published X9.24 test vectors.** If the exact vectors cannot be
sourced, say so explicitly rather than inventing them, and mark the implementation
`// SPEC-UNVERIFIED:`. This is the single most important place in the project not to
fabricate.

Optional stretch: AES DUKPT per X9.24-3, behind the same interface.

### PIN blocks (ISO 9564)
- Format 0 (PAN-XOR) and Format 4 (AES)
- Encode, encrypt under the DUKPT PIN key, decrypt and verify in the host simulator
- **PIN values and PIN blocks are never logged at any level.** Enforce with a test.

### Tokenization and the P2PE boundary
- `ITokenVault` — deterministic, format-preserving surrogate for a PAN (same length,
  passes Luhn, preserves last 4 for receipts)
- The OPT encrypts the PAN and issues a token. **Nothing downstream of the OPT ever
  holds a cleartext PAN.** The site controller, journal, dashboard, RabbitMQ payloads
  and logs see only tokens.
- Write a test that walks every persisted artefact and every log sink after a
  transaction and fails if an unmasked PAN appears anywhere.

### Key store
- `DpapiKeyStore` — Windows DPAPI-protected key material
- `InMemoryKeyStore` — must throw at construction if the build configuration is Release

### `docs/threat-model.md`
- STRIDE analysis over the four trust boundaries: card-to-OPT, OPT-to-site-controller,
  site-controller-to-host, operator-to-dashboard
- The Secure Cryptographic Device boundary and why it exists
- Key injection and key management lifecycle, and what a real deployment does that this
  simulation cannot
- A mapping table: PCI DSS requirement -> where this project addresses it -> what is
  deliberately out of scope
- An honest "what this simulation does not prove" section

## Tests
- DUKPT derivation against test vectors, including several counter advances
- PIN block round trip for both formats
- Token determinism, Luhn validity, and irreversibility without the vault
- The log-and-storage PAN scan test described above

## Demo — `scripts/demo-03.ps1`
Derive a key from a test BDK, show the KSN advancing across three transactions with
different derived keys, encrypt and decrypt a PIN block, and print a transaction record
showing the token in place of the PAN.
