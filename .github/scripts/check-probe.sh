#!/usr/bin/env bash
# The synthetic probe (README, "A synthetic order every minute"), on the Compose stack after the end-to-end
# tests. The stack was started without the probe's profile, so this is also where "off unless asked for" is
# proved, and everything the probe is for:
#  1. Without the profile there is no probe: Prometheus holds no probe target and no probe series, and no
#     synthetic alert fires.
#  2. Started with its profile, it completes the whole journey (sign in, product, order, confirmation,
#     cancellation) several times, through the gateway, and Prometheus finds it by DNS.
#  3. Its orders are left out of the numbers about the business (orders placed, outcomes, sales, payments)
#     but not out of the numbers about the system (the saga durations): it really did place them.
#  4. It runs unprivileged and publishes no port, and no synthetic alert fires while it works.
#  5. When it is removed, Prometheus drops the target (this is what SyntheticProbeStale relies on to look
#     back instead of at `up`).
# PROBE_EMAIL and PROBE_PASSWORD are in .env already: Order and Payment were started with the account to
# leave out. Reads the numbers from Prometheus.
set -euo pipefail

PROM=http://localhost:9090
compose() { docker compose --profile probe "$@"; }

value() { # a PromQL expression -> its number, or nothing
  curl --silent --max-time 5 --get --data-urlencode "query=$1" "$PROM/api/v1/query" \
    | jq -r '.data.result[0].value[1] // empty' 2>/dev/null || true
}
series() { # a PromQL expression -> how many series it returns
  curl --silent --max-time 5 --get --data-urlencode "query=$1" "$PROM/api/v1/query" \
    | jq -r '.data.result | length' 2>/dev/null || echo 0
}
whole() { printf '%.0f' "${1:-0}"; }

# the figures about the business, and about the system, in one line
business() {
  echo "placed=$(whole "$(value 'sum(shopeasy_orders_placed_total)')")" \
       "outcomes=$(whole "$(value 'sum(shopeasy_order_outcomes_total)')")" \
       "revenue=$(whole "$(value 'sum(shopeasy_orders_confirmed_amount_total)')")" \
       "payments=$(whole "$(value 'sum(shopeasy_payments_total)')")"
}
sagas() { whole "$(value 'sum(shopeasy_saga_duration_seconds_count)')"; }

fail() {
  echo "::error::$1"
  echo "--- probe log"; compose logs --no-color --tail 60 probe 2>&1 || true
  exit 1
}

# ── 1. no probe, no trace of it ──────────────────────────────────────────────
[ "$(series 'up{job="probe"}')" -eq 0 ] || fail "Prometheus lists a probe target on a stack started without the probe's profile"
[ "$(series '{__name__=~"shopeasy_probe_.*"}')" -eq 0 ] || fail "Prometheus holds probe series on a stack started without the probe"
[ "$(series 'ALERTS{alertname=~"Synthetic.*"}')" -eq 0 ] || fail "a synthetic alert is raised on a stack without the probe"
echo "Without the profile: no probe target, no probe series, no synthetic alert"

# When did Order and Payment start? Starting the probe must not re-create them: their counters would begin
# again at zero and the comparison below would prove nothing. (`up --build probe` would build and re-create
# the services it depends on too, since the build context now holds files the earlier steps made.)
started() { docker inspect -f '{{.State.StartedAt}}' "$(compose ps -q order)" "$(compose ps -q payment)" | tr '\n' ' '; }
started_before="$(started)"

# the figures, once they have stopped moving (the last test order is settled; Prometheus is 15 s behind)
before=""
for _ in $(seq 1 6); do
  now="$(business) sagas=$(sagas)"
  [ "$now" = "$before" ] && break
  before="$now"; sleep 17
done
echo "before the probe: $before"
placed_before="$(whole "$(value 'sum(shopeasy_orders_placed_total)')")"
[ "$placed_before" -gt 0 ] || fail "the end-to-end tests left no placed order in the metrics, so 'the probe's orders are not counted' would prove nothing"

# ── 2. the probe, with its profile ───────────────────────────────────────────
compose build probe
compose up -d --no-deps --wait probe
echo "The probe is running"

ok=0
for _ in $(seq 1 60); do
  ok="$(whole "$(value 'sum(shopeasy_probe_runs_total{result="success"})')")"
  [ "$ok" -ge 3 ] && break
  sleep 5
done
echo "complete journeys so far: $ok"
[ "$ok" -ge 3 ] || fail "the probe has not completed three runs in 5 minutes"

[ "$(series 'up{job="probe"} == 1')" -eq 1 ] || fail "Prometheus does not find the probe by DNS (job 'probe' is not up)"
[ "$(series 'shopeasy_probe_success == 1')" -eq 5 ] || fail "not all five steps of the last run are reported as working"
[ "$(series 'shopeasy_probe_success == 0')" -eq 0 ] || fail "a step of the last run failed"
[ "$(whole "$(value 'shopeasy_probe_consecutive_failures')")" -eq 0 ] || fail "the probe counts failed runs in a row"
age="$(whole "$(value 'time() - shopeasy_probe_last_success_timestamp_seconds')")"
[ "$age" -lt 120 ] || fail "the probe's last success is $age seconds old"
echo "All five steps work (last full success ${age} s ago); $(value 'shopeasy_probe_duration_seconds{step="confirmation"}') s to the confirmation"

# ── 3. business numbers unmoved, system numbers moved ────────────────────────
[ "$(started)" = "$started_before" ] || fail "starting the probe re-created Order or Payment, so their counters began again (before: $started_before, now: $(started))"
sleep 20   # a scrape after the last of those orders
after="$(business) sagas=$(sagas)"
echo "after the probe:  $after"
[ "${after% sagas=*}" = "${before% sagas=*}" ] || fail "the probe's orders changed the business numbers (PROBE_EMAIL is not told to Order and Payment?)"
sagas_before="${before##*sagas=}"
[ "$(sagas)" -ge $((sagas_before + 3)) ] || fail "the probe's orders do not show in the saga durations: did it really place them?"
echo "The probe's orders are left out of the business numbers and present in the saga durations ($sagas_before -> $(sagas))"

# ── 4. unprivileged, no port, no alert ───────────────────────────────────────
container="$(compose ps -q probe)"
uid="$(docker exec "$container" id -u)"
read_only="$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$container")"
new_privileges="$(docker inspect -f '{{json .HostConfig.SecurityOpt}}' "$container")"
echo "probe: uid=$uid read-only-rootfs=$read_only security-opt=$new_privileges"
[ "$uid" != "0" ] || fail "the probe runs as root"
[ "$read_only" = "true" ] || fail "the probe has a writable root file system"
[[ "$new_privileges" == *no-new-privileges* ]] || fail "the probe may gain privileges"

published="$(compose port probe 9091 2>/dev/null || true)"
if [ -n "$published" ] && [ "${published##*:}" != "0" ]; then fail "the probe publishes its metrics port to the host: $published"; fi
echo "The probe's metrics port is not published"

[ "$(series 'ALERTS{alertname=~"Synthetic.*",alertstate="firing"}')" -eq 0 ] || fail "a synthetic alert fires while the probe works"
echo "No synthetic alert fires"

# ── 5. gone ──────────────────────────────────────────────────────────────────
compose rm -sf probe
gone=1
for _ in $(seq 1 24); do
  gone="$(series 'up{job="probe"}')"
  [ "$gone" -eq 0 ] && break
  sleep 5
done
[ "$gone" -eq 0 ] || fail "Prometheus still lists the probe two minutes after it was removed"
echo "Removed: Prometheus no longer lists the probe"
