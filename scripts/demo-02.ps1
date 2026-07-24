# demo-02.ps1 — Phase 2 proof: BER-TLV, the virtual card, and the EMV terminal flow.
# Runs a full EMV online-authorisation transaction against the virtual card in-process,
# over the same ICardReader the physical PC/SC reader implements, and prints the complete
# APDU trace decoded by tag name plus the assembled ISO 8583 field 55, for three card
# profiles (contact, contactless, offline-decline). Runs unattended.
$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '=============================================================='
Write-Host ' OpenForecourt - Phase 2 demo (BER-TLV + virtual card + EMV)'
Write-Host '=============================================================='
Write-Host ''
Write-Host 'Test PANs only (CLAUDE.md section 7). Raw APDU hex is the in-boundary'
Write-Host 'card<->terminal wire; the decoded view masks the PAN (first 6 + last 4).'
Write-Host ''

dotnet run --project src/OpenForecourt.Opt --configuration Release -- --amount 6000
if ($LASTEXITCODE -ne 0) { throw 'Demo failed' }
