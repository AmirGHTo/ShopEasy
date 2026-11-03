#!/usr/bin/env bash
# The failure scenarios of the demo: one function to cause each, one to undo it, and what to watch while it
# lasts. Used by break.sh and fix.sh (README, "Traffic and failures to try"). Not meant to be run by itself.
#
# Every scenario is something the alerts are written for. The text printed names the alert, the dashboard
# and the place in the other tools, as the alert rules, the dashboards and the Alertmanager rules define
# them; none of it is a promise about timing on a slow machine ("after N minutes" is the alert's own `for:`).

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)

SCENARIOS="payment postgres rabbitmq order product-hang poison"

compose() { docker compose -f "$ROOT/docker-compose.yml" "$@"; }

# a value from .env, unless the environment already has it
setting() {
  local name=$1 current=${!1:-}
  if [ -n "$current" ]; then printf '%s' "$current"; return; fi
  [ -f "$ROOT/.env" ] && grep -E "^${name}=" "$ROOT/.env" | tail -n 1 | cut -d= -f2- || true
}

say() { printf '\n%s\n' "$*"; }

watch_with() {
  cat <<TXT

Where to look:
  Prometheus alerts   http://localhost:9090/alerts       (pending first, firing after the alert's "for")
  Alertmanager        http://localhost:9093              (a symptom held back by a known cause shows as inhibited)
  The mails           http://localhost:8025              (Mailpit)
  Grafana             http://localhost:3000              (ShopEasy Service Health / Order Flow / Dependencies)
  Traces, logs        http://localhost:16686  http://localhost:5341
Runbooks: README, "Runbooks". Undo it with:  ops/demo/fix.sh $1
Traffic to see it on:  ops/demo/load.sh --rate 20 --duration 600
TXT
}

# ── payment: the service is down ──────────────────────────────────────────────
break_payment() {
  compose stop payment
  say "Payment is stopped. Orders are accepted, their stock is reserved, and then they wait for a payment."
  cat <<'TXT'
Expect:  ServiceDown (job shopeasy-payment, after 1 minute)
         QueueWithoutConsumers (queue payment-process, after 2 minutes), held back by ServiceDown: Alertmanager shows it inhibited
         Order Flow dashboard: the "paid" line falls away while "stock reserved" goes on; the oldest order in progress rises
         the saga timeout service cancels what waits too long (README, "Saga timeout handling")
Explain: Jaeger, service order, an order placed now: the trace stops after the stock reservation; Seq: OrderId = '<id>'
TXT
}
fix_payment() { compose start payment; say "Payment is started. The waiting payments are processed; the queue drains and the alerts resolve."; }

# ── postgres: the database is down ────────────────────────────────────────────
break_postgres() {
  compose stop postgres
  say "PostgreSQL is stopped. Every service loses its database."
  cat <<'TXT'
Expect:  DependencyDown (job postgres: pg_up is 0, after 1 minute)
         placing an order fails (5xx at the gateway): ShopServerErrorsHigh needs 5 minutes and 20 requests, and is held back by DependencyDown
         Dependencies dashboard: connections in use fall to 0; Service Health: errors rise
Explain: Seq: @Level = 'Error' (connection refused / timeout); /health/ready of each service fails
TXT
}
fix_postgres() { compose start postgres; say "PostgreSQL is started. The services reconnect on their own (the pool drops dead connections)."; }

# ── rabbitmq: the broker is down ──────────────────────────────────────────────
break_rabbitmq() {
  compose stop rabbitmq
  say "RabbitMQ is stopped. Orders are still accepted; their messages wait in the Order outbox."
  cat <<'TXT'
Expect:  DependencyDown (job rabbitmq, after 1 minute)
         with traffic: SagaCommandsNotSent (a command waits more than 2 minutes) and OutboxBacklog (more than 50 events for 5 minutes)
         PollingLoopStalled for the dead-letter loop, which cannot read the broker
         (all three fire in Prometheus; Alertmanager holds their mails back while DependencyDown fires)
         Order Flow dashboard: the outbox and saga-command backlogs grow, no order moves on
Explain: Seq: the Order service logs the broker as not ready, once; nothing is lost: the outbox sends it all when the broker is back
TXT
}
fix_rabbitmq() { compose start rabbitmq; say "RabbitMQ is started. The outbox delivers what waited; the backlog drains and the orders finish."; }

# ── order: the service is killed ──────────────────────────────────────────────
break_order() {
  compose kill order
  say "The Order service is killed (no clean shutdown)."
  cat <<'TXT'
Expect:  ServiceDown (job shopeasy-order, after 1 minute)
         orders and the order list answer 5xx at the gateway: ShopServerErrorsHigh (5 minutes, 20 requests), held back by ServiceDown
         Service Health dashboard: the order instance is DOWN, requests per second for it fall to 0
Explain: Seq: the gateway's log shows the failed calls to order; the sagas it was running continue from the database when it is back
TXT
}
fix_order() { compose start order; say "The Order service is started. Its sagas continue from where the database says they were."; }

# ── product-hang: the service stops answering ─────────────────────────────────
break_product_hang() {
  compose pause product
  say "The Product service is frozen: it accepts connections and never answers."
  cat <<'TXT'
Expect:  ServiceDown (job shopeasy-product: its metrics time out, after 1 minute)
         every catalog request waits for the gateway's timeout (30 seconds) and then answers 504;
         with traffic: ShopServerErrorsHigh (5 minutes), ShopSlow (p95 above 2 seconds for 10 minutes); both fire in Prometheus and are
         held back in Alertmanager while ServiceDown fires
         Service Health dashboard: latency p95 climbs to the timeout; "5xx by endpoint" shows the product routes
Explain: Jaeger, service gateway, slowest traces: the span for the product call is the whole request
TXT
}
fix_product_hang() { compose unpause product; say "The Product service is running again."; }

# ── poison: a message nobody can process ──────────────────────────────────────
# A ProcessPaymentCommand without a user: the payment row cannot be saved (NOT NULL: SqlState 23502), which no
# retry can cure, so after its retries (about 15 seconds) the message goes to payment-process_error. Tried against
# the real Payment consumer and a real RabbitMQ.
POISON_QUEUE=payment-process
POISON_ERROR_QUEUE=payment-process_error

rabbit_api() { # method path [data]
  local user password url
  user=$(setting RABBITMQ_USER); password=$(setting RABBITMQ_PASSWORD)
  url=${RABBITMQ_MANAGEMENT_URL:-http://localhost:15672}
  curl --silent --show-error --fail --max-time 15 -u "$user:$password" -H 'content-type: application/json' \
    -X "$1" "$url/api$2" ${3:+--data "$3"}
}

poison_payload() {
  local order_id message_id
  order_id=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen)
  message_id=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen)
  # the envelope MassTransit reads (the services use its outbox, which needs a message id), as a JSON string
  # inside the management API's publish request
  printf '{"properties":{"content_type":"application/vnd.masstransit+json","delivery_mode":2},"routing_key":"%s","payload_encoding":"string","payload":"{\\"messageId\\":\\"%s\\",\\"messageType\\":[\\"urn:message:ShopEasy.Shared.Contracts:ProcessPaymentCommand\\"],\\"message\\":{\\"orderId\\":\\"%s\\",\\"userEmail\\":\\"demo@shopeasy.test\\",\\"currency\\":\\"USD\\",\\"amount\\":10}}"}' \
    "$POISON_QUEUE" "$message_id" "$order_id"
}

break_poison() {
  local routed
  routed=$(rabbit_api POST "/exchanges/%2F/amq.default/publish" "$(poison_payload)")
  case "$routed" in *'"routed":true'*) ;; *) echo "The broker did not route the message to $POISON_QUEUE: $routed" >&2; return 1;; esac
  say "A payment command without a user was put on $POISON_QUEUE. The Payment service retries it for about 15 seconds and gives up."
  cat <<TXT
Expect:  the message is in $POISON_ERROR_QUEUE after about 15 seconds: http://localhost:15672 (RabbitMQ), or the Dead Letter Monitor of the Order service (/deadletters, as an administrator)
         DeadLettersPresent (queue $POISON_ERROR_QUEUE, critical, after 5 minutes)
         Order Flow dashboard: the dead letters panel shows 1
Explain: Seq / the Payment log: 'null value in column "UserId" ... violates not-null constraint' (SqlState 23502), then R-FAULT and the move to the error queue
TXT
}
fix_poison() {
  rabbit_api DELETE "/queues/%2F/$POISON_ERROR_QUEUE/contents" >/dev/null
  say "The error queue $POISON_ERROR_QUEUE is emptied (a real dead letter is requeued from /deadletters once its cause is fixed; this one has none). DeadLettersPresent resolves."
}

# ── dispatch ──────────────────────────────────────────────────────────────────
usage() {
  echo "usage: ops/demo/$1.sh <scenario>"
  echo "scenarios: $SCENARIOS"
}

run_scenario() { # break|fix scenario
  local action=$1 scenario=${2:-}
  case " $SCENARIOS " in
    *" $scenario "*) ;;
    *) usage "$action" >&2; return 2;;
  esac
  command -v docker >/dev/null || { echo "docker is needed" >&2; return 1; }
  "${action}_${scenario//-/_}"
  if [ "$action" = break ]; then watch_with "$scenario"; fi
}
