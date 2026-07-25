#!/usr/bin/env bash
# demo-03.sh — Phase 3 proof: DUKPT, PIN blocks, and tokenization.
# Derives a key from the published X9.24 test BDK, shows the KSN advancing across three
# transactions with a different derived key each time, encrypts and decrypts a PIN block,
# and prints a transaction record showing the token in place of the PAN. Runs unattended.
set -euo pipefail

cd "$(dirname "$0")/.."

echo "=============================================================="
echo " OpenForecourt — Phase 3 demo (DUKPT + PIN blocks + tokens)"
echo "=============================================================="
echo
echo "Test keys and test PANs only (CLAUDE.md section 7). The BDK stays on the"
echo "host; the terminal holds only the IPEK and derives a fresh key per transaction."
echo

dotnet run --project src/OpenForecourt.CryptoDemo --configuration Release
