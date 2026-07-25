#!/usr/bin/env bash
# Phase 5 demo: bring up the forecourt with docker compose, run transactions on four pumps,
# kill the host simulator mid-run and show offline authorisation continuing, restart the site
# controller to show crash recovery, then restore the host and show replay + reconciliation.
set -euo pipefail

cd "$(dirname "$0")/../deploy"
BASE="http://localhost:8080"
COMPOSE="docker compose"

section() { echo; echo "=============================================================="; echo " $1"; echo "=============================================================="; }
totals()  { echo "totals: $(curl -s "$BASE/api/v1/totals")"; }
authorise() {
  curl -s -X POST "$BASE/api/v1/pumps/$1/authorise" \
    -H "Idempotency-Key: $2" -H "Content-Type: application/json" \
    -d "{\"amountMinor\":$3,\"token\":\"TOK-$1\"}"
  echo
}
wait_health() {
  for _ in $(seq 1 90); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health" 2>/dev/null)" = "200" ]; then
      echo "site controller healthy"; return 0
    fi
    sleep 2
  done
  echo "site controller did not become healthy"; $COMPOSE logs site-controller | tail -40; exit 1
}

section "Bring up the forecourt (RabbitMQ + host simulator + site controller)"
$COMPOSE up -d --build
wait_health

section "1) Four pumps authorise ONLINE (host up)"
for p in 1 2 3 4; do authorise "$p" "online-$p-$(date +%s%N)" 2500; done
totals

section "2) Kill the host simulator — authorisations continue OFFLINE under the floor limit"
$COMPOSE stop host-simulator
echo "waiting for the availability prober to detect the outage..."; sleep 6
for p in 1 2 3 4; do authorise "$p" "offline-$p-$(date +%s%N)" 1000; done
totals

section "3) Kill and restart the site controller — recovery reconciles on startup"
$COMPOSE restart site-controller
wait_health
echo "--- recovery log ---"
$COMPOSE logs site-controller 2>&1 | grep -i "recovery" | tail -8 || true
totals

section "4) Restore the host — offline transactions replay in order and reconcile"
$COMPOSE start host-simulator
echo "waiting for the prober to mark the host up and replay to drain..."; sleep 10
totals

section "Done"
echo "Tear down with:  (cd deploy && docker compose down -v)"
