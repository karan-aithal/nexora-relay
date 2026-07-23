#!/usr/bin/env bash
# demo-00.sh — Phase 0 proof: the solution structure exists and builds + tests cleanly.
# Runs unattended. Uses the CI solution filter so it works on Linux (Windows-only
# adapters excluded).
set -euo pipefail

cd "$(dirname "$0")/.."

echo "=============================================================="
echo " OpenForecourt — Phase 0 demo (scaffold and ports)"
echo "=============================================================="

echo
echo "## Solution projects"
dotnet sln OpenForecourt.sln list | grep -E 'csproj' | sort

echo
echo "## Port interfaces (the architectural seams)"
ls src/OpenForecourt.Abstractions/Ports

echo
echo "## Domain value types"
ls src/OpenForecourt.Abstractions/Domain

echo
echo "## Build (Linux CI filter — Windows adapters excluded)"
dotnet build OpenForecourt.CI.slnf --configuration Release

echo
echo "## Test"
dotnet test OpenForecourt.CI.slnf --no-build --configuration Release

echo
echo "Phase 0 demo complete: structure present, build clean, tests green."
