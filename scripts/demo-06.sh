#!/usr/bin/env bash
# Phase 6 demo: bring up the whole forecourt — RabbitMQ, the acquirer simulator, the site
# controller with the Angular operator console, the outdoor payment terminal and eight pump
# firmware processes — then drive a complete fuelling and two injected faults over the same
# API the browser uses, so the run proves the console is not the only way in.
#
# The demo is the browser: http://localhost:8080
set -euo pipefail

cd "$(dirname "$0")/../deploy"
BASE="http://localhost:8080"
API="$BASE/api/v1"
COMPOSE="docker compose"

section() { echo; echo "=============================================================="; echo " $1"; echo "=============================================================="; }
get()     { curl -s "$API/$1"; }
post()    { curl -s -X POST "$API/$1" -H 'Content-Type: application/json' -d "${2:-{\}}"; }
pump()    { curl -s "$API/pumps/$1"; }

wait_health() {
  for _ in $(seq 1 120); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health" 2>/dev/null)" = "200" ]; then
      echo "site controller healthy"; return 0
    fi
    sleep 2
  done
  echo "site controller did not become healthy"; $COMPOSE logs site-controller | tail -40; exit 1
}

# Polls one pump until a jq-style field matches, so the script never races the firmware.
wait_state() {
  local id=$1 want=$2
  for _ in $(seq 1 60); do
    if pump "$id" | grep -q "\"state\":\"$want\""; then return 0; fi
    sleep 0.5
  done
  echo "pump $id never reached $want; last state:"; pump "$id"; echo; return 1
}

section "Bring up the forecourt (RabbitMQ + acquirer + site controller + console + 8 firmware pumps)"
$COMPOSE up -d --build
wait_health
echo "site:  $(get site)"
echo
echo "The operator console is at $BASE — open it now and watch the rest of this run."
sleep 3

section "1) A customer pays at pump 1 (real EMV flow against the virtual card)"
post "opt/1/card" '{"cardId":"contactless-mc","amountMinor":4000,"entryMode":"Contactless"}'
echo
wait_state 1 Authorised
echo "pump 1: $(pump 1)"

section "2) Nozzle up — the firmware meters real volume over OFP-1"
post "simulator/pumps/1/nozzle/up" >/dev/null
wait_state 1 Dispensing
sleep 3
echo "pump 1 mid-dispense: $(pump 1)"

section "3) Nozzle down — the delivered value is what gets settled"
post "simulator/pumps/1/nozzle/down" >/dev/null
sleep 2
echo "pump 1: $(pump 1)"
TX=$(get transactions | grep -o '"transactionId":"[^"]*"' | head -1 | cut -d'"' -f4)
echo "transaction $TX"
echo "trace steps: $(get "transactions/$TX/trace" | grep -o '"kind":"[a-z0-9]*"' | sort | uniq -c | tr '\n' ' ')"
echo "totals: $(get totals)"

section "4) FAULT — corrupt the next OFP-1 frame's CRC on pump 2"
echo "The firmware drops the damaged frame and the manager retransmits under its own timeout."
post "faults/pumps/2/corrupt-crc" >/dev/null
post "opt/2/card" '{"cardId":"contactless-mc","amountMinor":3000,"entryMode":"Contactless"}'
echo
wait_state 2 Authorised && echo "pump 2 authorised anyway — the retry got through"

section "5) FAULT — kill the acquirer link; the site keeps selling fuel offline"
post "faults/host" '{"down":true}' >/dev/null
echo "waiting for the availability prober to notice..."; sleep 6
post "opt/3/card" '{"cardId":"contactless-mc","amountMinor":1500,"entryMode":"Contactless"}'
echo
echo "totals (note offlinePending and exposure): $(get totals)"

section "6) Restore the link — the offline authorisation replays and reconciles"
post "faults/host" '{"down":false}' >/dev/null
echo "waiting for the prober and the replayer..."; sleep 12
echo "totals: $(get totals)"

section "Done — the console is the demo"
echo "Open $BASE and try the fault console yourself:"
echo "  · drop a nozzle mid-dispense        · power-cut a pump (its process is killed)"
echo "  · force response 51 / 05 / 54 / 91  · suspend the site controller mid-authorisation"
echo "  · corrupt a frame CRC               · force a duplicate transaction"
echo
echo "Tear down with:  (cd deploy && docker compose down -v)"
