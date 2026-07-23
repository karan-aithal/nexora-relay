# demo-00.ps1 — Phase 0 proof: the solution structure exists and builds + tests cleanly.
# Runs unattended. Uses the CI solution filter (Windows-only adapters excluded) so the
# same script works on Linux CI and Windows dev machines.
$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '=============================================================='
Write-Host ' OpenForecourt - Phase 0 demo (scaffold and ports)'
Write-Host '=============================================================='

Write-Host "`n## Solution projects"
dotnet sln OpenForecourt.sln list | Select-String 'csproj' | Sort-Object

Write-Host "`n## Port interfaces (the architectural seams)"
Get-ChildItem src/OpenForecourt.Abstractions/Ports | Select-Object -ExpandProperty Name

Write-Host "`n## Domain value types"
Get-ChildItem src/OpenForecourt.Abstractions/Domain | Select-Object -ExpandProperty Name

Write-Host "`n## Build (Linux CI filter - Windows adapters excluded)"
dotnet build OpenForecourt.CI.slnf --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Write-Host "`n## Test"
dotnet test OpenForecourt.CI.slnf --no-build --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

Write-Host "`nPhase 0 demo complete: structure present, build clean, tests green."
