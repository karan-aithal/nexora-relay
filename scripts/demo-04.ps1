# demo-04.ps1 — Phase 4 proof: pump firmware <-> pump manager over TCP.
# Builds the firmware host (the same portable C core that would run on an STM32), then runs the
# pump manager against four firmware host processes: two concurrent fuellings with a live frame
# trace, a mid-dispense nozzle drop, and a comms outage with automatic recovery. Everything is
# simulated — no hardware. Runs unattended.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$fwDir = 'firmware/pump'
$buildDir = "$fwDir/build-host"

Write-Host '=============================================================='
Write-Host ' OpenForecourt — Phase 4 demo (pump firmware + manager)'
Write-Host '=============================================================='
Write-Host ''
Write-Host 'Building firmware host (CMake, HAL=host)...'
cmake -S $fwDir -B $buildDir -DHAL=host | Out-Null
cmake --build $buildDir | Out-Null

$exe = if ($IsWindows) { "$buildDir/pump_host.exe" } else { "$buildDir/pump_host" }
Write-Host "Firmware host built: $exe"
Write-Host ''

dotnet run --project src/OpenForecourt.PumpDemo --configuration Release -- $exe
