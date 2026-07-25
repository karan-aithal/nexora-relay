# demo-03.ps1 — Phase 3 proof: DUKPT, PIN blocks, and tokenization.
# Derives a key from the published X9.24 test BDK, shows the KSN advancing across three
# transactions with a different derived key each time, encrypts and decrypts a PIN block,
# and prints a transaction record showing the token in place of the PAN. Runs unattended.
$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '=============================================================='
Write-Host ' OpenForecourt - Phase 3 demo (DUKPT + PIN blocks + tokens)'
Write-Host '=============================================================='
Write-Host ''
Write-Host 'Test keys and test PANs only (CLAUDE.md section 7). The BDK stays on the'
Write-Host 'host; the terminal holds only the IPEK and derives a fresh key per transaction.'
Write-Host ''

dotnet run --project src/OpenForecourt.CryptoDemo --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Demo failed' }
