#!/usr/bin/env bash
# Places orders through the gateway at a chosen rate, with a mix of outcomes (confirmed, declined, cancelled),
# so that the dashboards have something to show. The orders are real: they count in the sales and payment figures.
#   ops/demo/load.sh [--rate ORDERS_PER_MINUTE] [--duration SECONDS] [--mix CONFIRMED,DECLINED,CANCELLED]
# Defaults: 12 a minute, until Ctrl+C, 70,10,20. The stack has to be up (docker compose up -d).
# It signs up new customers on every run; sign-up is limited to 10 an hour per client address, so a rate of
# 80 (the top) uses them all.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$ROOT"

if ! docker compose ps --status running --services 2>/dev/null | grep -qx gateway; then
  echo "The shop is not running. Start it first: docker compose up -d" >&2
  exit 1
fi

docker compose --profile load build loadgen
exec docker compose --profile load run --rm --no-deps loadgen "$@"
