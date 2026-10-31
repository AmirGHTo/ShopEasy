#!/usr/bin/env bash
# Proves the whole alerting chain on the running Compose stack:
#
#   Prometheus evaluates the rules  ->  Alertmanager holds the alert  ->  a mail reaches the mailbox
#
# The Watchdog alert always fires, so it is the probe: if its mail arrives in Mailpit, an alert that
# fires anywhere else would arrive too. (The failure tests that stop services raise ServiceDown and
# others as a side effect; nothing here depends on them.)
set -euo pipefail

prometheus=${PROMETHEUS_URL:-http://localhost:9090}
alertmanager=${ALERTMANAGER_URL:-http://localhost:9093}
mailpit=${MAILPIT_URL:-http://localhost:8025}

# wait_for "<what>" <command...>: tries every 2 seconds for up to 3 minutes
wait_for() {
  local what=$1; shift
  for _ in $(seq 1 90); do
    if "$@" > /dev/null 2>&1; then echo "OK: $what"; return 0; fi
    sleep 2
  done
  echo "::error::Gave up waiting for: $what"
  return 1
}

prometheus_sees_alertmanager() {
  curl -fsS "$prometheus/api/v1/alertmanagers" | python3 -c '
import json, sys
active = json.load(sys.stdin)["data"]["activeAlertmanagers"]
sys.exit(0 if any("alertmanager:9093" in a["url"] for a in active) else 1)'
}

alertmanager_holds_watchdog() {
  curl -fsS "$alertmanager/api/v2/alerts" | python3 -c '
import json, sys
alerts = json.load(sys.stdin)
sys.exit(0 if any(a["labels"].get("alertname") == "Watchdog" for a in alerts) else 1)'
}

mailbox_has_watchdog_mail() {
  curl -fsS "$mailpit/api/v1/messages" | python3 -c '
import json, sys
messages = json.load(sys.stdin)["messages"]
sys.exit(0 if any("watchdog" in m["Subject"].lower() for m in messages) else 1)'
}

wait_for "Prometheus has found Alertmanager" prometheus_sees_alertmanager
wait_for "Alertmanager holds the Watchdog alert" alertmanager_holds_watchdog
wait_for "the Watchdog mail is in the mailbox" mailbox_has_watchdog_mail
