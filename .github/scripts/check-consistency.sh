#!/usr/bin/env bash
# After the end-to-end tests: while they placed their orders, the Order service's consistency check
# ran in the Compose stack. It compared every settled order with what Payment holds (a real call with
# a token the Order service got for itself) and found nothing wrong.
#  1. The check has examined at least as many orders as the tests got accepted. (The CI .env makes it
#     judge an order 10 s after it settled and run every 15 s, so this waits seconds, not minutes.)
#  2. No rule is broken.
# Reads the numbers from Prometheus, which scrapes the Order service's metrics port.
set -euo pipefail

value() { # a PromQL expression -> its number, or nothing
  curl --silent --max-time 5 --get --data-urlencode "query=$1" http://localhost:9090/api/v1/query \
    | jq -r '.data.result[0].value[1] // empty' 2>/dev/null || true
}
whole() { printf '%.0f' "${1:-0}"; }

placed=0
checked=0
for _ in $(seq 1 36); do
  placed="$(whole "$(value 'sum(shopeasy_orders_placed_total)')")"
  checked="$(whole "$(value 'sum(shopeasy_consistency_orders_checked_total)')")"
  [ "$placed" -gt 0 ] && [ "$checked" -ge "$placed" ] && break
  sleep 5
done
echo "orders accepted: $placed, orders the consistency check examined: $checked"

if [ "$placed" -eq 0 ] || [ "$checked" -lt "$placed" ]; then
  echo "::error::the consistency check has not examined the orders the tests placed"
  docker compose logs --no-color --tail 40 order | grep -i -E 'consistency|failed' || true
  exit 1
fi

broken="$(value 'max(shopeasy_consistency_violations)')"
echo "largest number of orders breaking one rule: ${broken:-none}"
if [ -z "$broken" ] || [ "$(whole "$broken")" -ne 0 ]; then
  echo "::error::the consistency check found orders that disagree with their payment"
  docker compose logs --no-color order | grep 'CONSISTENCY VIOLATION' || true
  exit 1
fi
