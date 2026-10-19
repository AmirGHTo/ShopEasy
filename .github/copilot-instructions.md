# ShopEasy: instructions for Copilot

.NET 8 microservices: Gateway, Identity, Product, Order, Inventory, Payment. PostgreSQL, RabbitMQ with
MassTransit, Redis. The Order service runs a saga (orchestrator, consumers, timeout service, outbox).
Reliability and self-healing matter more than anything else here. Write in English.

## Your role: reviewer only

Comment on pull requests. Do not change code, push, or open pull requests. Review the lines the pull
request changes, not the rest of the repository.

## What to suggest

1. **Simpler, cleaner code.** Show the smaller version in a few lines, not a rewrite. Look for:
   duplicated code that could have one copy; dead code, unused parameters or settings; an abstraction
   with a single user; a long method doing several things; a comment that only restates the code;
   anything cleverer than it needs to be.
2. **Reliability problems with a concrete failure path:** the input or event, the state it leaves
   behind, and what the customer or operator then sees. Examples: a failure that stops something for
   good (a loop whose exception ends the host, a call without a timeout, an unbounded retry, a saga that
   can stay in one state forever); a consumer or command that is not safe to run twice or in any order
   (check the state or ledger row; a message id only deduplicates the same message); an outage turned
   into a lost order, a double charge or double stock (money and stock changes need a transaction and a
   row lock); a timeout that can fire while the system itself was down; a failure nobody would notice
   (a swallowed exception, a metric that is never published); a setting that needs a rebuild to change.

Do not comment on naming, formatting or style, and do not suggest new dependencies, moving code between
projects, or changes that would grow the pull request well beyond about 100 lines.

## How changes are made here

- One concern per pull request. A setting is visible in four places: the service's `appsettings.json`,
  the Compose environment (`Section__Key: "${ENV_NAME:-default}"`), a commented line in `.env.example`,
  and `k8s/configmaps/configmap.yaml`, plus a row in the README table "Service configuration keys".
- A test must run the code that runs in production and must fail when the fix is removed. Once a test
  went through a test-only copy of a method while the production copy was broken.
- Tests that need real PostgreSQL or RabbitMQ are skipped unless `SHOPEASY_TEST_POSTGRES` /
  `SHOPEASY_TEST_RABBITMQ` are set; CI sets them and fails on any skipped test.
- The README describes what the system does now: no tutorial wording, no plans or to-dos.
- Secrets never go in the repository. `EXPOSE_ACCESS_TOKEN`, `SEED_DEMO_USERS` and
  `GRAFANA_ANONYMOUS_ENABLED` stay off on anything reachable from the internet.
