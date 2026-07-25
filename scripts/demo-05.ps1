# Phase 5 demo: bring up the forecourt with docker compose, run transactions on four pumps,
# kill the host simulator mid-run and show offline authorisation continuing, restart the site
# controller to show crash recovery, then restore the host and show replay + reconciliation.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..' 'deploy')
$Base = 'http://localhost:8080'

function Section($t) { "`n=============================================================="; " $t"; "==============================================================" }
function Totals { "totals: " + (Invoke-RestMethod "$Base/api/v1/totals" | ConvertTo-Json -Compress) }
function Authorise($pump, $key, $amount) {
    $body = @{ amountMinor = $amount; token = "TOK-$pump" } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post "$Base/api/v1/pumps/$pump/authorise" `
        -Headers @{ 'Idempotency-Key' = $key } -ContentType 'application/json' -Body $body | ConvertTo-Json -Compress
}
function Wait-Health {
    for ($i = 0; $i -lt 90; $i++) {
        try { if ((Invoke-WebRequest "$Base/health" -UseBasicParsing).StatusCode -eq 200) { 'site controller healthy'; return } } catch { }
        Start-Sleep 2
    }
    throw 'site controller did not become healthy'
}

Section 'Bring up the forecourt (RabbitMQ + host simulator + site controller)'
docker compose up -d --build
Wait-Health

Section '1) Four pumps authorise ONLINE (host up)'
1..4 | ForEach-Object { Authorise $_ "online-$_-$([DateTimeOffset]::UtcNow.Ticks)" 2500 }
Totals

Section '2) Kill the host simulator - authorisations continue OFFLINE under the floor limit'
docker compose stop host-simulator
'waiting for the availability prober to detect the outage...'; Start-Sleep 6
1..4 | ForEach-Object { Authorise $_ "offline-$_-$([DateTimeOffset]::UtcNow.Ticks)" 1000 }
Totals

Section '3) Kill and restart the site controller - recovery reconciles on startup'
docker compose restart site-controller
Wait-Health
'--- recovery log ---'
docker compose logs site-controller 2>&1 | Select-String -Pattern 'recovery' | Select-Object -Last 8
Totals

Section '4) Restore the host - offline transactions replay in order and reconcile'
docker compose start host-simulator
'waiting for the prober to mark the host up and replay to drain...'; Start-Sleep 10
Totals

Section 'Done'
'Tear down with:  docker compose -f deploy/docker-compose.yml down -v'
