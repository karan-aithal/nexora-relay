# demo-01.ps1 — Phase 1 proof: the ISO 8583 codec and the acquiring host simulator.
# Starts the host, sends an echo, an approval, a duplicate, two declines, a timeout and a
# reversal from a test client, and prints the decoded field-by-field trace of every message
# in both directions. Runs unattended.
$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '=============================================================='
Write-Host ' OpenForecourt - Phase 1 demo (ISO 8583 codec + host simulator)'
Write-Host '=============================================================='
Write-Host ''
Write-Host 'Test PANs only (CLAUDE.md section 7). PANs are masked in every trace.'
Write-Host ''

dotnet run --project src/OpenForecourt.HostSimulator --configuration Release -- demo
if ($LASTEXITCODE -ne 0) { throw 'Demo failed' }
