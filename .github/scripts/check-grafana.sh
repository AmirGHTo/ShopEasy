#!/usr/bin/env bash
# After `docker compose up --wait` with Grafana, Prometheus and Jaeger among the services: the data
# sources Grafana was provisioned with really work, and the links between the three signals are in place.
#
#  1. Grafana has both data sources (Prometheus and Jaeger) and each one reports itself healthy: a wrong
#     address or a broken provisioning file shows here, not on the first day somebody opens Explore.
#  2. The Prometheus data source links an exemplar (a point on a latency graph with a trace behind it)
#     to Jaeger and to the log lines of the same trace in Seq.
#  3. Jaeger offers "Logs of this trace in Seq" on every trace page.
#
# (That an exemplar really leads to a trace is checked by check-exemplars.py, after the end-to-end tests
# have made some requests.)
set -euo pipefail

GRAFANA=${GRAFANA_URL:-http://localhost:3000}
JAEGER=${JAEGER_URL:-http://localhost:16686}
password="${GRAFANA_ADMIN_PASSWORD:-$(grep -E '^GRAFANA_ADMIN_PASSWORD=' .env | cut -d= -f2-)}"
api() { curl --silent --fail --max-time 10 --user "admin:$password" "$GRAFANA$1"; }

for _ in $(seq 1 30); do
  curl --silent --fail --max-time 5 "$GRAFANA/api/health" >/dev/null 2>&1 && break
  sleep 3
done
curl --silent --fail --max-time 5 "$GRAFANA/api/health" >/dev/null || { echo "::error::Grafana does not answer at $GRAFANA"; exit 1; }

# ── 1. both data sources exist and are healthy ───────────────────────────────
failed=0
for uid in prometheus jaeger; do
  status=""
  for _ in $(seq 1 12); do
    status="$(api "/api/datasources/uid/$uid/health" 2>/dev/null | jq -r '.status // empty' || true)"
    [ "$status" = "OK" ] && break
    sleep 5
  done
  if [ "$status" = "OK" ]; then
    echo "Grafana data source '$uid' is healthy"
  else
    echo "::error::Grafana data source '$uid' is not healthy (status: ${status:-no answer})"
    api "/api/datasources/uid/$uid/health" 2>&1 || true
    failed=1
  fi
done

# ── 2. the exemplar links of the Prometheus data source ──────────────────────
destinations="$(api /api/datasources/uid/prometheus | jq -c '.jsonData.exemplarTraceIdDestinations // []')"
if [ "$(jq '[.[] | select(.name == "trace_id" and .datasourceUid == "jaeger")] | length' <<<"$destinations")" -ge 1 ]; then
  echo "An exemplar links to the trace in Jaeger"
else
  echo "::error::The Prometheus data source has no exemplar link to Jaeger: $destinations"; failed=1
fi
if [ "$(jq '[.[] | select(.name == "trace_id" and ((.url // "") | contains("TraceId")))] | length' <<<"$destinations")" -ge 1 ]; then
  echo "An exemplar links to the log lines of its trace in Seq"
else
  echo "::error::The Prometheus data source has no exemplar link to Seq: $destinations"; failed=1
fi

# ── 3. Jaeger's link to Seq ──────────────────────────────────────────────────
if curl --silent --fail --max-time 10 "$JAEGER/" | grep -q 'Logs of this trace in Seq'; then
  echo "Jaeger offers 'Logs of this trace in Seq'"
else
  echo "::error::Jaeger's page does not carry the link pattern from jaeger/ui.json (QUERY_UI_CONFIG)"; failed=1
fi

exit "$failed"
