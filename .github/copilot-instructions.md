# ShopEasy: instructions for Copilot

.NET 8 microservices: Gateway, Identity, Product, Order, Inventory, Payment. PostgreSQL, RabbitMQ with
MassTransit, Redis. The Order service runs a saga (orchestrator, consumers, timeout service, outbox).
Reliability and self-healing matter more than anything else here. Write in English.

## What to look for

Report only problems with a concrete failure path: the input or event, the state it leaves behind, and
what the customer or operator then sees. Skip style comments and naming.

- A failure that stops something for good: a background loop whose exception ends the host, a call
  without a timeout, a retry without a limit or without a pause, a saga that can stay in one state forever.
- Consumers and commands must be safe to run twice and in any order. Check the state or ledger row before
  acting; a message id only deduplicates the same message.
- A database or broker outage must be waited out, not turned into a lost order, a double charge or
  double stock. Money and stock changes need a transaction and a row lock.
- A clock-based rule (a timeout, an expiry) that can fire while the system itself was down.
- A failure nobody would notice: swallowed exceptions, a metric that is never published, no log line.
- Settings that cannot be changed without rebuilding the image.

## How changes are made here

- One concern per pull request, about 100 lines. Split anything larger. Short description: what was wrong,
  what changed, what was tested.
- A setting is visible in four places: the service's `appsettings.json`, the Compose environment
  (`Section__Key: "${ENV_NAME:-default}"`), a commented line in `.env.example`, and
  `k8s/configmaps/configmap.yaml`. Add a row to the README table "Service configuration keys".
- A test must run the code that runs in production, and must fail when the fix is removed. Once a test
  went through a test-only copy of a method and the production copy was broken.
- Real PostgreSQL and RabbitMQ tests live in `ShopEasy.UnitTests/Infrastructure` and are skipped unless
  `SHOPEASY_TEST_POSTGRES` / `SHOPEASY_TEST_RABBITMQ` are set. CI runs them and fails on any skipped test.
- The README describes what the system does now. No tutorial wording, no "step N", no plans or to-dos.
- Secrets never go in the repository (`.env` is git-ignored). Do not enable `EXPOSE_ACCESS_TOKEN`,
  `SEED_DEMO_USERS` or `GRAFANA_ANONYMOUS_ENABLED` on anything reachable from the internet.
