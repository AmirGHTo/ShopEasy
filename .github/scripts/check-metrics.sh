#!/usr/bin/env bash
# After `docker compose up --wait` with Prometheus among the services:
#  1. Prometheus reaches every target: the metrics port (9091) of the six application containers, and
#     RabbitMQ, PostgreSQL (through its exporter) and Redis (through its exporter).
#  2. The numbers the alerts and dashboards are built on really exist. An alert written for a metric
#     name that is not there never fires and says nothing, so each name is asked for by name.
#  3. The ports of the three dependencies are not published to the host: only Prometheus may read them.
#  4. The queue alert does not fire on a healthy stack (every queue that is read has a consumer).
#  5. The API port of none of the application containers serves the metrics: the numbers must only be
#     readable on the metrics port, which Compose does not publish. The status code is no judge of that
#     (the gateway answers an address nothing claims with the storefront page, 200), so the body is
#     checked: the Prometheus text format has "# HELP" / "# TYPE" lines.
set -euo pipefail

PROM=http://localhost:9090
jobs=(shopeasy-gateway shopeasy-product shopeasy-order shopeasy-inventory shopeasy-payment shopeasy-identity
      rabbitmq postgres redis)

query() { # PromQL -> the number of series it returns
  curl --silent --max-time 5 --get --data-urlencode "query=$1" "$PROM/api/v1/query" \
    | jq -r '.data.result | length' 2>/dev/null || echo 0
}

# ── 1. every target is up ────────────────────────────────────────────────────
down=""
for _ in $(seq 1 30); do
  down=""
  for job in "${jobs[@]}"; do
    up="$(query "up{job=\"$job\"} == 1")"
    [ "$up" -ge 1 ] || down="$down $job"
  done
  [ -z "$down" ] && break
  sleep 5
done

if [ -n "$down" ]; then
  echo "::error::Prometheus does not reach:$down"
  curl --silent "$PROM/api/v1/targets" \
    | jq -r '.data.activeTargets[] | select(.health != "up") | "\(.labels.job): \(.health) \(.lastError)"'
  exit 1
fi
echo "Prometheus scrapes all ${#jobs[@]} targets"

# ── 2. the numbers exist ─────────────────────────────────────────────────────
# (the exporters' own queries and the first per-queue figures come with the second scrape)
wanted=(
  'pg_up == 1'                                  # PostgreSQL answers the exporter
  pg_connections_used pg_connections_max        # PostgresConnectionsHigh (postgres-exporter-queries.yml)
  pg_transaction_oldest_seconds
  pg_stat_database_deadlocks pg_stat_database_blks_hit pg_stat_database_blks_read
  'redis_up == 1'
  redis_memory_used_bytes redis_connected_clients redis_evicted_keys_total
  redis_keyspace_hits_total redis_keyspace_misses_total
  rabbitmq_queue_messages_ready rabbitmq_queue_messages_unacked   # one series per queue (rabbitmq/90-shopeasy.conf)
  'rabbitmq_queue_consumers{queue="payment-process"} >= 1'       # the Payment service is reading its queue
  'rabbitmq_queue_consumers{queue="inventory-reserve"} >= 1'
)
missing=""
for _ in $(seq 1 12); do
  missing=""
  for expression in "${wanted[@]}"; do
    [ "$(query "$expression")" -ge 1 ] || missing="$missing
  $expression"
  done
  [ -z "$missing" ] && break
  sleep 5
done
if [ -n "$missing" ]; then
  echo "::error::Prometheus holds no series for:$missing"
  exit 1
fi
echo "The ${#wanted[@]} series the dependency alerts and dashboards need are all there"

# ── 3. the dependencies' metrics ports stay inside the network ───────────────
failed=0
for target in rabbitmq:15692 postgres-exporter:9187 redis-exporter:9121; do
  service="${target%%:*}"; port="${target##*:}"
  published="$(docker compose port "$service" "$port" 2>/dev/null || true)"
  # nothing, or ":0" (what some Compose versions print), means the port is not published
  if [ -n "$published" ] && [ "${published##*:}" != "0" ]; then
    echo "::error::$service publishes its metrics port ($port) to the host: $published"; failed=1
  fi
done
[ "$failed" = 0 ] && echo "RabbitMQ, PostgreSQL and Redis metrics are not published to the host"

# ── 4. nothing is read by nobody on a healthy stack ──────────────────────────
# the same expression as QueueWithoutConsumers, without its 2 minutes of patience
without=""
for _ in $(seq 1 12); do
  without="$(curl --silent --max-time 5 --get \
      --data-urlencode 'query=rabbitmq_queue_consumers{queue!~".*_(error|skipped)"} == 0' "$PROM/api/v1/query" \
    | jq -r '[.data.result[].metric.queue] | join(", ")' 2>/dev/null || echo '')"
  [ -z "$without" ] && break
  sleep 5
done
if [ -n "$without" ]; then
  echo "::error::Queues without a consumer on a healthy stack (QueueWithoutConsumers would fire): $without"
  failed=1
else
  echo "Every queue that is not an error queue has a consumer"
fi

# ── 5. the API port serves no metrics ────────────────────────────────────────
for service in identity gateway product inventory payment order; do
  port="$(docker compose port "$service" 8080 | awk -F: '{print $NF}')"
  body="$(mktemp)"
  status="$(curl --silent --output "$body" --write-out '%{http_code}' --max-time 10 "http://localhost:$port/metrics")"
  echo "$service: GET /metrics on the API port -> $status"
  if grep -qE '^# (HELP|TYPE) ' "$body"; then
    echo "::error::$service serves its metrics on its API port"; failed=1
  fi
  rm -f "$body"
done
exit "$failed"
