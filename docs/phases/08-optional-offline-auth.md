# Phase 8 (Optional) — Offline Data Authentication

**Only attempt this if Phases 0–7 are complete and time remains.** It is the deepest
EMV content in the project and the easiest place to lose a week.

**Goal:** Full offline card authentication, which is only buildable *because* the cards
are simulated — with a real card you could never hold the issuer private key.

Make that point explicitly in the walkthrough. It reframes simulation as an advantage.

## Deliverables
- A self-generated three-tier key hierarchy: scheme CA -> issuer -> ICC
- Issuer public key certificate and ICC public key certificate, correctly formatted per
  the EMV certificate structures
- **SDA** — signed static application data verification
- **DDA** — INTERNAL AUTHENTICATE with a dynamic signature over an unpredictable number
- **CDA** — combined DDA with application cryptogram generation
- Virtual card personalisation tool that generates a card profile with valid
  certificates from the test CA
- Terminal-side CA public key store, keyed by RID and CA public key index
- Negative tests: revoked CA index, tampered static data, wrong ICC key, expired
  certificate — each must fail authentication for the correct documented reason

## Warning
Certificate recovery is fiddly and specification-exact. Where a detail cannot be
verified against the specification, mark it `// SPEC-UNVERIFIED:` and record it. Do not
guess at certificate formats.
