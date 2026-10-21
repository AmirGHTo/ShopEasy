#!/usr/bin/env bash
# After `docker compose up --wait` with Prometheus among the services:
#  1. Prometheus reaches the metrics port (9091) of all six application containers.
#  2. The API port of none of them serves /metrics: the numbers must only be readable on the metrics
#     port, which Compose does not publish.
set -euo pipefail

targets=6
up=0
for _ in $(seq 1 30); do
  up="$(curl --silent --max-time 5 http://localhost:9090/api/v1/targets \
        | jq -r '[.data.activeTargets[] | select(.health == "up")] | length' 2>/dev/null || echo 0)"
  [ "$up" = "$targets" ] && break
  sleep 5
done

if [ "$up" != "$targets" ]; then
  echo "::error::Prometheus reaches $up of $targets targets"
  curl --silent http://localhost:9090/api/v1/targets \
    | jq -r '.data.activeTargets[] | select(.health != "up") | "\(.labels.job): \(.health) \(.lastError)"'
  exit 1
fi
echo "Prometheus scrapes all $targets targets"

failed=0
for service in identity gateway product inventory payment order; do
  port="$(docker compose port "$service" 8080 | awk -F: '{print $NF}')"
  status="$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 10 "http://localhost:$port/metrics")"
  echo "$service: GET /metrics on the API port -> $status"
  [ "$status" != "200" ] || { echo "::error::$service serves /metrics on its API port"; failed=1; }
done
exit "$failed"
