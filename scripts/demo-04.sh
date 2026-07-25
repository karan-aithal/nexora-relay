#!/usr/bin/env bash
# demo-04.sh — Phase 4 proof: pump firmware ↔ pump manager over TCP.
# Builds the firmware host (the same portable C core that would run on an STM32), then runs
# the pump manager against four firmware host processes: two concurrent fuellings with a live
# frame trace, a mid-dispense nozzle drop, and a comms outage with automatic recovery.
# Everything is simulated — no hardware. Runs unattended.
set -euo pipefail

cd "$(dirname "$0")/.."

FW_DIR=firmware/pump
BUILD_DIR="$FW_DIR/build-host"

echo "=============================================================="
echo " OpenForecourt — Phase 4 demo (pump firmware + manager)"
echo "=============================================================="
echo
echo "Building firmware host (CMake, HAL=host)..."
cmake -S "$FW_DIR" -B "$BUILD_DIR" -DHAL=host >/dev/null
cmake --build "$BUILD_DIR" >/dev/null
echo "Firmware host built: $BUILD_DIR/pump_host"
echo

dotnet run --project src/OpenForecourt.PumpDemo --configuration Release -- "$BUILD_DIR/pump_host"
