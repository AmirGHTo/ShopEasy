# Presentation outline: "How we see it, how we are told, how we find the cause"

For the development team. About 40 minutes with two live demos and questions. The story has four beats: **what can fail, how we see it, how we are told, how we find the cause.** Everything shown is in the repository; nothing needs a slide that is not here. Prepare with the stack running and `ops/demo/load.sh --rate 20 --duration 2400` started 10 minutes before, so the graphs have a past.

| # | Minutes | Beat | What to say | What to show |
|---|---|---|---|---|
| 1 | 3 | **Why** | The patterns in this project (retry, saga, outbox) hide failures: a retry that works is invisible, one that does not must wake somebody. Observability is how they become *visible, explainable and alarming*. | The [guide's](README.md#what-it-is-for) three questions |
| 2 | 4 | **What can fail** | A shop is only as good as the paths through it. Six are scripted for the demo: a service stopped, one killed, one frozen, the database down, the broker down, a message nobody can process. | [What happens when a part fails](../../README.md#what-happens-when-a-part-fails) |
| 3 | 4 | **Three signals, one id** | Logs say what happened, traces say what called what and for how long, metrics say how much over time. One correlation id and one trace id join them. | The [picture](README.md#the-stack-in-one-picture) |
| 4 | 6 | **Demo 1: follow an order** | Place an order. Seq by `OrderId`, Jaeger by `order.id`, the Saga Monitor timeline. One order is one trace across the gateway, the outbox, the saga and every message. | The steps in [Follow one order](README.md#follow-one-order-through-logs-traces-and-metrics) |
| 5 | 4 | **How we are told** | Rules in Prometheus, Alertmanager groups and silences, a mail with a runbook link. Critical needs a person now (mailed within seconds); warning is grouped and repeated every four hours. The Watchdog proves the chain. | Mailpit; one runbook ([ServiceDown](../../README.md#servicedown)) |
| 6 | 8 | **Demo 2: break Payment** | `ops/demo/break.sh payment` under load. `ServiceDown` goes pending, then firing; its mail arrives; the symptom alert is shown as inhibited. Order Flow: orders stop at "stock reserved". Jaeger: the trace stops. `fix.sh payment`. | [scenarios.md](scenarios.md), first row |
| 7 | 4 | **Dashboards by question** | Overview: is something wrong? Service Health: which service? Order Flow: where in the order's life? Dependencies: which dependency? Synthetic Orders: can a customer buy right now? SLOs: is the month at risk? | The six dashboards, one minute each at most |
| 8 | 3 | **The customer's view** | Every alert watches something from the inside; none says whether a customer can buy. A probe orders through the gateway every minute and cancels. Its orders are kept out of the sales figures. The error budget turns "some errors" into "how fast we spend the month". | [A synthetic order every minute](../../README.md#a-synthetic-order-every-minute), [Service level objectives](../../README.md#service-level-objectives); optional (set `PROBE_EMAIL` and `PROBE_PASSWORD` in `.env` and start it before the talk with `docker compose --profile probe up -d --build`): `docker compose stop payment` and `SyntheticOrderFailing` after three runs |
| 9 | 2 | **Why you can trust it** | The alert rules have `promtool` tests, every alert has a runbook (CI checks both), and CI starts the real stack and asks Prometheus, Grafana and Jaeger what they hold. | `prometheus-rules.test.yml`, [`.github/scripts/`](../../.github/scripts) |
| 10 | 2 | **What it does not do** | Mail, not a pager. Nothing in Kubernetes. Governance (log retention, personal data in logs, an audit log) left out. | [What it does not do](README.md#what-it-does-not-do) |
| 11 | - | **Try it** | Fifteen minutes from a fresh clone to a working alert. | [Your first fifteen minutes](README.md#your-first-fifteen-minutes) |

## If you have a second demo slot

`ops/demo/break.sh poison`: a message that cannot be processed lands in `payment-process_error`; `DeadLettersPresent` fires after 5 minutes (so start it early, or show the Dead Letter Monitor while you talk about something else).

## Likely questions

- *Why not alert on every error?* Because an error that a retry cured is the pattern working; an alert must mean someone has to act. Alerts have a `for:` time and, for errors, a minimum number of requests (`ShopServerErrorsHigh` needs 5 minutes and 20 requests); the budget alerts ask two windows at once, so a blip that costs nothing raises neither.
- *Where would a pager go?* A receiver in `alertmanager.yml`; credentials stay out of the file.
- *What does it cost to run?* On a laptop: Prometheus, the Collector, Jaeger, Seq, Grafana, Alertmanager, Mailpit and two exporters, beside the shop. The services' own overhead is the metrics endpoint and the spans (`Telemetry__SamplingRatio` cuts the volume).
- *Does it work in Kubernetes?* The services do; the stack is not shipped there.
