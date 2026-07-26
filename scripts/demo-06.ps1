# Phase 6 demo: bring up the whole forecourt — RabbitMQ, the acquirer simulator, the site
# controller with the Angular operator console, the outdoor payment terminal and eight pump
# firmware processes — then drive a complete fuelling and two injected faults over the same
# API the browser uses, so the run proves the console is not the only way in.
#
# The demo is the browser: http://localhost:8080
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..' 'deploy')
$Base = 'http://localhost:8080'
$Api = "$Base/api/v1"

function Section($t) { "`n=============================================================="; " $t"; "==============================================================" }
function Get-Api($path) { Invoke-RestMethod "$Api/$path" }
function Post-Api($path, $body = '{}') {
    Invoke-RestMethod -Method Post "$Api/$path" -ContentType 'application/json' -Body $body
}
function Show($label, $value) { "$label" + ($value | ConvertTo-Json -Compress -Depth 6) }

function Wait-Health {
    for ($i = 0; $i -lt 120; $i++) {
        try { if ((Invoke-WebRequest "$Base/health" -UseBasicParsing).StatusCode -eq 200) { 'site controller healthy'; return } } catch { }
        Start-Sleep 2
    }
    throw 'site controller did not become healthy'
}

function Wait-State($id, $want) {
    for ($i = 0; $i -lt 60; $i++) {
        if ((Get-Api "pumps/$id").state -eq $want) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "pump $id never reached $want"
}

Section 'Bring up the forecourt (RabbitMQ + acquirer + site controller + console + 8 firmware pumps)'
docker compose up -d --build
Wait-Health
Show 'site:  ' (Get-Api 'site')
''
"The operator console is at $Base — open it now and watch the rest of this run."
Start-Sleep 3

Section '1) A customer pays at pump 1 (real EMV flow against the virtual card)'
Show 'opt: ' (Post-Api 'opt/1/card' '{"cardId":"contactless-mc","amountMinor":4000,"entryMode":"Contactless"}')
Wait-State 1 'Authorised'
Show 'pump 1: ' (Get-Api 'pumps/1')

Section '2) Nozzle up — the firmware meters real volume over OFP-1'
Post-Api 'simulator/pumps/1/nozzle/up' | Out-Null
Wait-State 1 'Dispensing'
Start-Sleep 3
Show 'pump 1 mid-dispense: ' (Get-Api 'pumps/1')

Section '3) Nozzle down — the delivered value is what gets settled'
Post-Api 'simulator/pumps/1/nozzle/down' | Out-Null
Start-Sleep 2
Show 'pump 1: ' (Get-Api 'pumps/1')
$tx = (Get-Api 'transactions')[0].transactionId
"transaction $tx"
$trace = Get-Api "transactions/$tx/trace"
'trace steps: ' + (($trace.steps | Group-Object kind | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')
Show 'totals: ' (Get-Api 'totals')

Section '4) FAULT — corrupt the next OFP-1 frame CRC on pump 2'
'The firmware drops the damaged frame and the manager retransmits under its own timeout.'
Post-Api 'faults/pumps/2/corrupt-crc' | Out-Null
Post-Api 'opt/2/card' '{"cardId":"contactless-mc","amountMinor":3000,"entryMode":"Contactless"}' | Out-Null
Wait-State 2 'Authorised'
'pump 2 authorised anyway — the retry got through'

Section '5) FAULT — kill the acquirer link; the site keeps selling fuel offline'
Post-Api 'faults/host' '{"down":true}' | Out-Null
'waiting for the availability prober to notice...'; Start-Sleep 6
Post-Api 'opt/3/card' '{"cardId":"contactless-mc","amountMinor":1500,"entryMode":"Contactless"}' | Out-Null
Show 'totals (note offlinePending and exposure): ' (Get-Api 'totals')

Section '6) Restore the link — the offline authorisation replays and reconciles'
Post-Api 'faults/host' '{"down":false}' | Out-Null
'waiting for the prober and the replayer...'; Start-Sleep 12
Show 'totals: ' (Get-Api 'totals')

Section 'Done — the console is the demo'
"Open $Base and try the fault console yourself:"
'  · drop a nozzle mid-dispense        · power-cut a pump (its process is killed)'
'  · force response 51 / 05 / 54 / 91  · suspend the site controller mid-authorisation'
'  · corrupt a frame CRC               · force a duplicate transaction'
''
'Tear down with:  (cd deploy; docker compose down -v)'
