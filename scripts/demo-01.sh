#!/usr/bin/env bash
# demo-01.sh — Phase 1 proof: the ISO 8583 codec and the acquiring host simulator.
# Starts the host, sends an echo, an approval, a duplicate, two declines, a timeout and a
# reversal from a test client, and prints the decoded field-by-field trace of every message
# in both directions. Runs unattended.
set -euo pipefail

cd "$(dirname "$0")/.."

echo "=============================================================="
echo " OpenForecourt — Phase 1 demo (ISO 8583 codec + host simulator)"
echo "=============================================================="
echo
echo "Test PANs only (CLAUDE.md section 7). PANs are masked in every trace."
echo

dotnet run --project src/OpenForecourt.HostSimulator --configuration Release -- demo
