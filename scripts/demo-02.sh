#!/usr/bin/env bash
# demo-02.sh — Phase 2 proof: BER-TLV, the virtual card, and the EMV terminal flow.
# Runs a full EMV online-authorisation transaction against the virtual card in-process,
# over the same ICardReader the physical PC/SC reader implements, and prints the complete
# APDU trace decoded by tag name plus the assembled ISO 8583 field 55, for three card
# profiles (contact, contactless, offline-decline). Runs unattended.
set -euo pipefail

cd "$(dirname "$0")/.."

echo "=============================================================="
echo " OpenForecourt — Phase 2 demo (BER-TLV + virtual card + EMV)"
echo "=============================================================="
echo
echo "Test PANs only (CLAUDE.md section 7). Raw APDU hex is the in-boundary"
echo "card<->terminal wire; the decoded view masks the PAN (first 6 + last 4)."
echo

dotnet run --project src/OpenForecourt.Opt --configuration Release -- --amount 6000
