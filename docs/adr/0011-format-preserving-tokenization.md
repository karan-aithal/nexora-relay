# 0011 — Deterministic format-preserving tokenization at the OPT boundary

## Context

The P2PE story (CLAUDE.md §7.4) requires that nothing downstream of the OPT holds a cleartext
PAN. The OPT exchanges the PAN for a token via `ITokenVault`. Phase 3 fixes the token's
properties: deterministic (same PAN → same token within a vault), format-preserving (same
length, all digits), passes Luhn, preserves the last four for receipts, and reveals nothing
about the PAN (irreversible without the vault).

## Decision

`FpeTokenVault` builds a token as: **keep the real last four**, fill the remaining leading
positions with digits derived from `HMAC-SHA256(perInstanceKey, panDigits)`, then **nudge one
middle digit so the whole token passes Luhn**. The token→PAN map is an in-memory dictionary;
detokenization is a lookup in it.

- **Determinism** comes from the HMAC over the PAN under a key fixed per vault instance — the
  same PAN always hashes to the same middle digits, so the same token. This matches the
  `ITokenVault` contract ("stable per PAN within a vault instance").
- **Irreversibility without the vault** comes from two facts: the middle digits are HMAC output
  under a key only the vault holds, so they cannot be reproduced; and the token→PAN mapping
  lives only inside the vault. A different vault (different key, empty map) cannot detokenize.
- **Luhn validity with last-four preserved**: adjusting a single digit cycles its Luhn
  contribution through all ten residues mod 10 (the doubled-digit mapping is also a bijection
  mod 10), so exactly one candidate for that one middle digit makes the checksum zero. The
  preserved last four are never touched.

## Consequences

- The token is a drop-in surrogate: same length, Luhn-valid, receipt-friendly — downstream code
  and message formats that expect a PAN-shaped field accept it unchanged.
- Tests assert determinism, length, Luhn, last-four preservation, distinctness across PANs, and
  cross-vault irreversibility. The PAN-scan test confirms no cleartext PAN reaches any artefact.
- This is **simulation-grade**: an in-memory map and an ephemeral HMAC key. A production vault is
  an HSM-backed format-preserving-encryption service with a durable, access-controlled mapping.
  The walkthrough lists this as a known weakness.

## Alternatives considered

- **Format-preserving encryption (FF1/FF3-1).** The "correct" production primitive, but heavy to
  implement correctly and easy to get subtly wrong — precisely the kind of from-memory crypto
  §11 warns against. The HMAC-plus-map approach gives every property the phase asks for with code
  that is obviously correct and testable. `// ponytail:` the vault is deterministic-by-HMAC, not
  true FPE; swap in an HSM FF1 service at the same `ITokenVault` seam if real FPE is ever needed.
- **Random token + map only (no derived middle).** Would not be deterministic without reading
  the map first; the HMAC makes determinism a property of the algorithm, not of lookup order.
- **Preserve the BIN as well as the last four.** Not required by the phase, and preserving the
  BIN leaks the issuer; only the last four (already printed on receipts) are preserved.
