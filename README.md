# ShopEasy — Production-Grade Microservices E-Commerce Platform

An e-commerce backend built with **.NET 8** and **C#** as independent microservices: distributed transactions (saga + outbox), event-driven messaging, CQRS, resilience, observability, and container orchestration.

---

## Table of Contents

1. [What This Is](#what-this-is)
2. [Architecture Overview](#architecture-overview)
3. [The Services](#the-services)
4. [Technology Stack](#technology-stack)
5. [Patterns & Mechanisms Explained](#patterns--mechanisms-explained)
6. [Getting Started](#getting-started)
7. [Configuration & Secrets](#configuration--secrets)
8. [How to Use It](#how-to-use-it)
9. [Order API](#order-api)
10. [Inventory Stock Model](#inventory-stock-model)
11. [Developer Portal & Monitoring](#developer-portal--monitoring)
12. [Testing](#testing)
13. [Horizontal Scaling](#horizontal-scaling)
14. [Kubernetes Deployment](#kubernetes-deployment)
15. [Project Structure](#project-structure)
16. [Troubleshooting](#troubleshooting)
17. [Production Hardening Roadmap & Known Limitations](#production-hardening-roadmap--known-limitations)

---

## What This Is

ShopEasy is an online store backend split into independent microservices. A customer can browse products, place an order, and have that order automatically validated, paid for, and fulfilled — with full failure recovery if anything goes wrong along the way.

The core workflow is **what happens behind a single "Place Order" request**: a distributed transaction spans four services (Order, Inventory, Payment, and back to Order), coordinated by a saga, with automatic compensation, refunds, and timeout handling if any step fails.

---

## Architecture Overview

```
                          ┌──────────────┐
                          │   Customer   │
                          └──────┬───────┘
                                 │ HTTPS
                                 ▼
                       ┌───────────────────┐
                       │   API Gateway     │  YARP + BFF
                       │   (port 5000)     │  Auth, rate limiting, routing
                       └─────────┬─────────┘
                                 │
         ┌──────────────┬────────┼────────┬──────────────┐
         ▼              ▼        ▼         ▼              ▼
   ┌──────────┐  ┌──────────┐ ┌──────┐ ┌─────────┐ ┌──────────┐
   │ Identity │  │ Product  │ │Order │ │Inventory│ │ Payment  │
   │  (5001)  │  │  (5101)  │ │(5102)│ │ (5103)  │ │  (5104)  │
   └────┬─────┘  └────┬─────┘ └──┬───┘ └────┬────┘ └────┬─────┘
        │             │          │          │           │
        │ each service owns its own database (DB-per-service)
        ▼             ▼          ▼          ▼           ▼
   ┌─────────────────────────────────────────────────────────┐
   │              PostgreSQL (5 separate databases)           │
   └─────────────────────────────────────────────────────────┘

   Async communication via RabbitMQ (events + commands)
   Caching via Redis · Tracing via Jaeger · Metrics via Prometheus
   Logs via Seq · Service-to-service via gRPC
```

**Two communication styles:**

- **Synchronous** — Gateway → services over HTTP; Order → Product over HTTP (authoritative price lookup when an order is placed); Order → Inventory over gRPC (fast, typed, binary).
- **Asynchronous** — services publish/consume events through RabbitMQ. This is how the saga orchestrates the order lifecycle without services being tightly coupled.

---

## The Services

| Service | Port | Responsibility |
|---|---|---|
| **Identity** | 5001 | OAuth2 / OpenID Connect provider (Duende IdentityServer). Issues JWTs. |
| **Gateway** | 5000 | Single entry point. YARP reverse proxy + Backend-for-Frontend. Handles auth, rate limiting, routing, and hosts the developer monitoring portal. |
| **Product** | 5101 | Product catalog CRUD. API-versioned (v1/v2). Redis-cached. |
| **Order** | 5102 | Order placement and orchestration. Validates requests, prices orders from the Product service, and owns the saga, outbox, and CQRS read model. Horizontally scalable. |
| **Inventory** | 5103 (REST) / 5105→8090 (gRPC) | Stock levels, reservation, commit, release. Exposes both REST and gRPC. |
| **Payment** | 5104 | Payment processing and refunds (simulated provider). |

**Infrastructure containers:** PostgreSQL, pgAdmin, RabbitMQ, Redis, Redis Insight, Jaeger, Prometheus, Grafana, Seq.

---

## Technology Stack

**Core**
- .NET 8 / C# 12
- ASP.NET Core Web API
- Entity Framework Core 8 (with Npgsql for PostgreSQL)

**Data**
- PostgreSQL 16 — one database per service
- Redis 7 — distributed caching

**Messaging**
- RabbitMQ 3.13 — message broker
- MassTransit — messaging abstraction over RabbitMQ

**Communication**
- YARP — reverse proxy gateway
- gRPC — internal service-to-service calls (Order → Inventory)
- Polly — HTTP resilience (retries, circuit breakers)

**Identity & Security**
- Duende IdentityServer — OAuth2 / OIDC
- JWT Bearer authentication
- Backend-for-Frontend (BFF) pattern

**Observability**
- Serilog + Seq — structured centralized logging
- OpenTelemetry + Jaeger — distributed tracing
- prometheus-net + Prometheus + Grafana — metrics & dashboards

**Testing**
- xUnit, FluentAssertions, NSubstitute — unit tests
- EF Core InMemory provider — isolated test databases
- Integration tests against running services

**Orchestration**
- Docker & Docker Compose — local/dev
- Kubernetes — production deployment (manifests included)

---

## Patterns & Mechanisms Explained

This is the heart of the project. Each pattern below is fully implemented.

### 1. API Gateway (YARP + BFF)

A single entry point (`localhost:5000`) that routes requests to internal services. It also implements the **Backend-for-Frontend** pattern: the browser holds only a secure session cookie, while the gateway holds the actual JWT and attaches it to downstream calls. This keeps tokens out of the browser entirely.

**Why it matters:** clients talk to one URL, never to internal services directly. Cross-cutting concerns (auth, rate limiting, CORS) live in one place.

### 2. Authentication & Authorization (OAuth2 / OIDC / JWT)

Identity issues JWTs via the OpenID Connect authorization-code flow. The gateway exchanges the code for tokens and stores them server-side. Services validate JWTs independently. Service-to-service calls (Order → Inventory) use the **Client Credentials** grant — a separate machine-to-machine token with no user involved.

### 3. Database per Service

Each service owns its own PostgreSQL database (`shopeasy_orders`, `shopeasy_inventory`, etc). No service reads another service's tables directly. This is the foundation of microservice independence — a schema change in one service can never break another.

### 4. Saga Pattern (Orchestration-based)

A "Place Order" is a **distributed transaction** across Order, Inventory, and Payment. Since there's no shared database transaction, we use a **saga** — a state machine that coordinates the steps and compensates (undoes) them on failure.

**Happy path:**
```
OrderConfirmed → ReserveInventory → ProcessPayment → CommitInventory → InventoryCommitted → Completed
```

**Saga states:** `Started → ReservingInventory → ProcessingPayment → Completing → Completed`
(or the compensation branch: `→ ReleasingInventory → Cancelled/Failed`)

The saga only becomes `Completed` (and the order `Confirmed`) when the Inventory service has **confirmed the stock commit** with an `InventoryCommittedEvent`. While it waits it is in `Completing`; if no answer arrives the timeout service re-sends the commit (safe, because commit is idempotent).

Each handler only acts when the saga is in the state its event is valid for. A duplicate or late event is ignored (or compensated), never allowed to move an order backwards. The saga state is always saved **before** the next command is published, so a fast reply can never arrive while the saga still shows the old state.

**Compensation matrix — every failure has a defined recovery:**

| Failure point | Inventory | Payment | Recovery action |
|---|---|---|---|
| Stock check fails | not reserved | not charged | Cancel order |
| Payment declined | reserved | not charged | Release inventory → Cancel |
| Timeout waiting for payment | reserved | not charged | Release inventory → Cancel |
| Late payment after cancel | released | charged | **Refund** → stay Cancelled |
| Commit fails transiently after payment (DB hiccup) | reserved | charged | Message retried, then re-sent by the timeout service (commit is idempotent) |
| Commit impossible after payment (reservation missing or already released) | not committed | charged | **Refund** → Failed / order Cancelled |
| Duplicate "payment succeeded" event | unchanged | charged | Ignored — never refunds an order that is completing or completed |
| Customer cancels before payment | reserved or nothing | not charged | Release inventory → Cancelled (see [Cancelling an order](#cancelling-an-order)) |
| Customer cancels while payment is in flight | reserved | may still be charged | Release inventory; a payment that succeeds is **refunded** → Cancelled |
| Customer cancels after payment | committed or committing | charged | Rejected with `409` (no returns flow yet) |

### 5. Outbox Pattern

When an order is saved, the event that starts the saga is written to an **outbox table in the same database transaction** as the order itself. A background service then publishes outbox entries to RabbitMQ.

**Why:** it guarantees that "order saved" and "event published" are atomic. If RabbitMQ is down when the order is placed, the event still gets delivered once the broker recovers — no lost orders, no phantom events.

The delivery worker only publishes while the message bus is **ready** (pattern 23), so a broker outage or a start-up race is never counted as a failed delivery attempt. If publishing genuinely fails, the retry count is stored; after 5 failed attempts an entry is parked as a dead letter (visible in the Outbox Monitor) and no longer blocks the entries behind it. The Payment and Inventory services have their own transactional outbox (pattern 22).

### 6. Distributed Outbox Locking

When the Order service runs as **multiple instances**, they'd all try to publish the same outbox messages. We use PostgreSQL's `SELECT ... FOR UPDATE SKIP LOCKED` so each message is claimed by exactly one instance; others skip locked rows. The database row lock *is* the distributed mutex — no external coordination service (ZooKeeper, etcd) needed.

### 7. Idempotent Consumers

Every message consumer must be safe to run twice, because RabbitMQ guarantees only *at-least-once* delivery. How each one does it:

- **Saga handlers** check the saga's current state (see pattern 4).
- **Inventory reserve** records a `ProcessedEvents` row per order, checked while the stock rows are locked.
- **Inventory commit / release** read the order's own ledger rows (`StockTransactions` for that order: reserved / committed / released) under the row locks, so a duplicate is detected exactly and only re-sends its result event. See [Inventory Stock Model](#inventory-stock-model).
- **Payment** keeps a `ProcessedEvents` row per order.
- **Payment and Inventory** additionally use MassTransit's **inbox** (pattern 22): a redelivered copy of the *same message* is recognised by its message id and not consumed again. The business-level checks above remain the safety net for *different* messages about the same order (for example the saga re-sending a command after a timeout).

### 8. Zombie Message Guard

A subtle distributed-systems bug: a payment command sent *before* a saga timed out can arrive *after* the saga was already cancelled. Without a guard, this "zombie" message would complete a cancelled order. Each consumer re-reads the saga state inside a transaction (with the saga row locked, see pattern 21) and, if the saga has already given up on the order, issues a compensating action (refund / release) instead of proceeding. Duplicates of events for orders that are completing or completed are ignored instead (a duplicate must never refund a paid, finished order).

### 9. Saga Timeout Handling

A background `SagaTimeoutService` scans every 30 seconds for sagas stuck in a non-terminal state longer than 2 minutes, and applies the correct compensation based on which state they're stuck in. This prevents orders from hanging forever if a downstream service dies permanently.

### 10. CQRS (Command Query Responsibility Segregation)

Reads and writes use **different models**:
- **Write side** — normalized `Orders` + `OrderItems` tables, optimized for consistency.
- **Read side** — a denormalized `OrderSummaries` table, pre-computed (status name, formatted amount, item summary, payment status), optimized for fast display with no joins.

The read model is updated synchronously on placement and asynchronously via saga events. Same database, separate tables (a separate read database is a one-config-change upgrade for later).

### 11. Eventual vs Strong Consistency (Hybrid Inventory Check)

The stock check before accepting an order uses a **layered hybrid** approach:
- **Layer 1** — Redis cache (~1ms, eventual, 30s TTL)
- **Layer 2** — gRPC to Inventory on cache miss (~150ms, strong)
- **Layer 3** — stale cache fallback (5min TTL) if Inventory is down, so orders still flow
- **Layer 4** — reject only if no data at all
- **Layer 5** — the saga's `ReserveInventory` does the authoritative check inside the Inventory service's database transaction, with the stock rows locked (`SELECT … FOR UPDATE`), so concurrent reservations — on any Inventory instance — are serialized per product and cannot oversell. See [Inventory Stock Model](#inventory-stock-model).

This decouples order acceptance from the Inventory service being online, while the saga still prevents overselling. This is the industry-standard approach for high-volume commodity e-commerce.

### 12. gRPC Internal Communication

Order → Inventory stock checks use gRPC instead of REST: a binary, strongly-typed contract (`inventory.proto`) with a single batch call for multiple products. ~3× faster than JSON/HTTP and the contract is compile-time enforced. REST stays available on a separate port for external clients.

### 13. Resilience (Polly)

HTTP clients use Polly pipelines: exponential-backoff retries and circuit breakers. MassTransit consumers use message retry with exponential backoff before sending to dead-letter queues.

### 14. Dead Letter Queues

Messages that fail repeatedly (after 3 retries with exponential backoff) land in `*_error` queues. A `DeadLetterMonitor` background service in the Order service checks them every 30 seconds and logs a `DEAD LETTER ALERT` for every queue that holds messages. The Dead Letter Monitor page and `GET /api/outbox/deadletters` (admin) list them, and `POST /api/outbox/deadletters/{queue}/requeue` (admin) moves the messages back to the queue they came from once the cause is fixed — see [Working with dead letters](#working-with-dead-letters).

This talks **AMQP** — the protocol and credentials the services already use for messaging — and does **not** need the RabbitMQ management plugin (HTTP, port 15672). That UI stays available as a tool for humans, but nothing in the application depends on it.

### 15. Rate Limiting

The gateway enforces tiered limits and returns `429` with `Retry-After` when one is exceeded:

| Policy | Applies to | Limit |
|---|---|---|
| `anonymous` | public routes (products, login, health) | 30/min per client IP |
| `authenticated` | everything behind login, **including reading and cancelling orders** | 100/min per user (admin 500) |
| `orders` | **order placement only** (`POST /api/orders`) | 10/min per user |

Users are identified by the `sub` claim and admins by the `role` claim of their login session, so the per-user and admin limits depend on the gateway copying those claims (it does; see [Troubleshooting](#troubleshooting) if `/account/me` shows no user id after an upgrade). The Order service has its own 10/min limit on placement as a second line of defence.

### 16. Redis Caching

Products (5-min TTL) and inventory (30-sec TTL) are cached, with `X-Cache: HIT/MISS` headers. Caches are invalidated on writes. Connection uses `abortConnect=false` so the app degrades gracefully if Redis is briefly unavailable.

### 17. API Versioning

Product service exposes `v1` and `v2` simultaneously (`/api/v1/products`, `/api/v2/products`). v2 adds formatted price, currency, and audit fields. Swagger documents both.

### 18. Health Checks

Every service exposes `/health`, `/health/ready`, and `/health/live`. Used by Docker healthchecks and Kubernetes liveness/readiness probes.

### 19. Observability — the three pillars

- **Logs** — Serilog ships structured logs to Seq, enriched with a `Service` property and a propagated `X-Correlation-Id` so a single request can be traced across all services.
- **Traces** — OpenTelemetry exports spans to Jaeger; you can see a request fan out across services on a timeline.
- **Metrics** — custom Prometheus counters (`orders_placed_total`, `outbox_pending_messages`, `sagas_active`, `saga_duration_seconds`, `inventory_cache_hits_total`) visualized in Grafana.

### 20. Server-Side Pricing & Request Validation

A client must never decide what it pays. `POST /api/orders` only accepts **what to buy** (`productId` + `quantity`); the product **name and unit price are always looked up from the Product service** and any `unitPrice` / `productName` sent by the client are ignored. The request is also validated (non-empty, size limits, quantity range, duplicate products merged) before anything is written.

**Design decision — what if the Product service is down?** The order is **rejected with HTTP 503** (`Retry-After: 5`), not accepted with a cached or client-supplied price. A stale or guessed price ends up on a payment, which is far more expensive to fix than asking the caller to retry. The lookup uses tight timeouts, 2 retries and a circuit breaker so a failing Product service fails fast instead of hanging customers. See the [Order API](#order-api) for the exact rules and responses.

### 21. Saga Row Locking

Several actors change a saga: the event consumers, the orchestrator that starts it, the timeout service and customer cancellation. Every transition therefore runs in a database transaction and loads the saga with `SELECT … FOR UPDATE` (`ISagaLocking`, `Sagas/SagaLocking.cs`), so a payment result racing the timeout, a cancel racing the orchestrator, or two Order instances starting the same saga are serialized: the second actor waits and then sees the first one's outcome. Commands are published only after the transaction commits.

### 22. Transactional Inbox/Outbox (Payment & Inventory)

A consumer that changes its database *and* publishes a result event has a classic gap: a crash between the commit and the publish loses the event, a failure after the publish leaves an event for a change that was rolled back. Payment and Inventory close it with MassTransit's **Entity Framework inbox/outbox** (`AddEntityFrameworkOutbox` + `UseEntityFrameworkOutbox` on every consumer endpoint):

1. MassTransit opens **one database transaction per message** (isolation `READ COMMITTED`, which the Inventory row locks need).
2. The consumer's changes — the payment row, the stock changes and ledger — an **inbox** row for the message, and the **events the consumer publishes** (stored in the `OutboxMessage` table instead of being sent) are all written in that transaction.
3. After the commit MassTransit sends the stored events to RabbitMQ and removes them. If the process dies or the broker is unreachable at that moment, the command was never acknowledged, so RabbitMQ redelivers it; the inbox shows it was already consumed, the consumer is **not** run again, and only the delivery of the stored events is completed.

Result: a payment or stock change is never recorded without its event, and no event is sent for work that was rolled back. This holds when the service is killed between the commit and the delivery, and when RabbitMQ is stopped at that moment: after the restart exactly one event arrives and the payment or stock change is not repeated. The all-or-nothing behaviour is covered by tests against a real PostgreSQL (see *Testing*).

The consumers call `db.Database.BeginOrJoinAsync()` (`Data/UnitOfWork.cs`): inside the outbox they **join** MassTransit's transaction; without it (unit tests) they open and own one. Tables: `InboxState`, `OutboxMessage` and `OutboxState` in the Payment and Inventory databases; old rows are cleaned up by MassTransit (duplicate-detection window: 30 minutes). The Order service keeps its own hand-written outbox (pattern 5) for the event that starts the saga.

### 23. Broker Readiness (no management API)

The Order service's outbox worker asks `IBrokerReadiness` before it publishes. The implementation reads **MassTransit's own bus health check** (`masstransit-bus`): healthy only when the bus is started, the RabbitMQ connection is up and every receive endpoint has declared its queue and bindings. The last part matters at start-up — an event published to an exchange that has no queue bound yet is silently dropped by the broker. While the bus is not ready, entries simply stay pending (one warning when the state changes, not one per cycle) and no retry is spent. This replaces the previous design, which polled the RabbitMQ management HTTP API (`:15672`) with Basic auth.

---

## Getting Started

### Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (with Compose)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (only needed for running tests / EF migrations locally)
- ~6 GB free RAM (the full stack runs ~15 containers)

### Run the entire system

```bash
git clone <your-repo-url>
cd ShopEasy

# Create your local config — every secret lives in .env (git-ignored), never in the repo
cp .env.example .env
#   → edit .env and replace every "change-me-*" value
#   → for local demos also set SEED_DEMO_USERS=true, SEED_ADMIN_PASSWORD, SEED_CUSTOMER_PASSWORD
#     and EXPOSE_ACCESS_TOKEN=true (needed for the Swagger "Authorize" flow)

docker-compose up --build
```

First build takes a few minutes. When it settles, every service will have run its database migrations automatically against PostgreSQL.

> Every setting and secret is explained in [Configuration & Secrets](#configuration--secrets).

### Verify it's up

Open the developer portal:

```
http://localhost:5000
```

### Stop it

```bash
docker-compose down
```

To also wipe the database volume:

```bash
docker-compose down -v
```

### Database migrations

Each service applies its EF Core migrations automatically at start-up. To change a schema, add a **new** migration from that service's folder (never delete and re-create `InitialCreate` on a database that holds data):

```bash
cd src/Services/ShopEasy.OrderService      # or ShopEasy.ProductService / InventoryService / PaymentService
dotnet ef migrations add <DescriptiveName> --output-dir Data/Migrations
# Identity lives in src/Identity/ShopEasy.Identity with the same command
```

This needs the `dotnet-ef` tool (`dotnet tool install --global dotnet-ef`) and a configured connection string (see *Running a service outside Docker* above).

---

## Configuration & Secrets

No secret lives in the repository. Docker Compose reads them from a local, git-ignored **`.env`** file; Kubernetes reads them from a Secret you create from a template.

### Docker Compose: the `.env` file

```bash
cp .env.example .env     # Windows cmd: copy .env.example .env
```

`docker-compose` refuses to start (and names the missing variable) if a required value is not set. Validate your file with `docker-compose config > /dev/null` (`> nul` on Windows).

| Variable | Required | Purpose |
|---|---|---|
| `POSTGRES_PASSWORD` | yes | Password of the `shopeasy` Postgres user. Also embedded in every service connection string, so **avoid `;` and `$`**. |
| `RABBITMQ_USER` / `RABBITMQ_PASSWORD` | yes | RabbitMQ credentials used by the broker and by Order, Inventory and Payment. |
| `RABBITMQ_ERLANG_COOKIE` | yes | Shared secret for RabbitMQ node clustering. Any long random string. |
| `GATEWAY_CLIENT_SECRET` | yes | OAuth2 client secret: Identity registers it, the Gateway presents it. |
| `ORDER_SERVICE_CLIENT_SECRET` | yes | OAuth2 client secret for the Order service's machine-to-machine token. |
| `PGADMIN_PASSWORD` | yes | pgAdmin login password (user `admin@shopeasy.com`). |
| `GRAFANA_ADMIN_PASSWORD` | yes | Grafana `admin` password. |
| `GRAFANA_ANONYMOUS_ENABLED` | no (`false`) | Allow read-only anonymous Grafana access. Keep `false` outside your laptop. |
| `SEED_DEMO_USERS` | no (`false`) | `true` creates `alice@shopeasy.com` (admin) and `bob@shopeasy.com` (customer) at Identity startup. **Local development only.** |
| `SEED_ADMIN_PASSWORD` / `SEED_CUSTOMER_PASSWORD` | only if seeding | Passwords for the demo users. Must satisfy the Identity policy: at least 8 characters with an uppercase letter, a lowercase letter and a digit. |
| `EXPOSE_ACCESS_TOKEN` | no (`false`) | `true` enables `GET /account/token` on the gateway, which returns the raw JWT so you can paste it into Swagger's *Authorize* button (it is not needed for the browser console examples in this README). Off by default because the BFF pattern exists to keep tokens out of the browser. While it is off, `/account/token` answers `404` with a JSON message explaining how to turn it on. After changing it, recreate the gateway: `docker-compose up -d gateway`. |

Generate strong random values, e.g. `openssl rand -base64 24`, or in PowerShell: `-join ((48..57)+(65..90)+(97..122) | Get-Random -Count 32 | % {[char]$_})`.

**Changing `POSTGRES_PASSWORD` after the first run:** Postgres only reads it when it creates an empty data volume. Either keep the old password, or wipe local data with `docker-compose down -v` and start again.

### Service configuration keys

Services read standard ASP.NET Core configuration, so every key can be an environment variable (`:` becomes `__`). Compose and the k8s manifests already set these; this is the reference if you deploy somewhere else.

| Key | Service | Notes |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | all with a DB | **Required.** Each service has its own database. No default. |
| `IdentityServer__Authority` | all APIs, Gateway | Internal URL of Identity, e.g. `http://identity:8080`. |
| `IdentityServer__PublicUrl` | Gateway | Browser-facing Identity URL (used for login redirects), e.g. `http://localhost:5001`. |
| `Oidc__ClientSecret` | Gateway | **Required.** Must equal `Clients__Gateway__Secret`. |
| `Clients__Gateway__Secret`, `Clients__OrderService__Secret` | Identity | **Required.** Secrets of the registered OAuth2 clients. |
| `Clients__Gateway__PublicUrl` | Identity | Browser-facing gateway URL for redirect URIs. Default `http://localhost:5000`. |
| `Seed__Enabled`, `Seed__AdminPassword`, `Seed__CustomerPassword` | Identity | Demo users, see above. Default off. |
| `Features__ExposeAccessToken` | Gateway | Enables `/account/token`. Default off. |
| `RabbitMQ__Host`, `RabbitMQ__Username`, `RabbitMQ__Password` | Order, Inventory, Payment | Username and password are **required** (no `guest` fallback). |
| `RabbitMQ__Port` | Order | AMQP port used by the dead-letter tooling. Default `5672`. |
| `RabbitMQ__ManagementUiUrl` | Order | Optional, e.g. `http://localhost:15672`. Only adds a "View" link to the dead-letter page; the service never calls it. |
| `Metrics__Port` | Order, Inventory, Payment | Port of the Prometheus metrics listener. Default `9091`; change it when running several services on one machine. |
| `ServiceAuth__ClientId`, `ServiceAuth__ClientSecret` | Order | **Required** when the service token is used. |
| `Services__ProductService` | Order | Product service base URL used for price lookup. Default `http://product:8080`. |
| `Services__InventoryService`, `Services__InventoryGrpcService` | Order | Inventory REST and gRPC URLs. |
| `Redis__Connection` | Product, Inventory, Order | Redis endpoint, e.g. `redis:6379`. |
| `Telemetry__Endpoint` | all | OTLP endpoint (Jaeger), default `http://jaeger:4317`. |
| `Testing__ForcePaymentSuccess` | Payment | Test-only: forces simulated payments to succeed. |

### Kubernetes

```bash
cp k8s/secrets/secrets.example.yaml k8s/secrets/secrets.yaml   # secrets.yaml is git-ignored
# replace every CHANGE_ME (connection strings must embed the same postgres password)
kubectl apply -f k8s/secrets/secrets.yaml
```

For real clusters prefer an external secret store (External Secrets, Sealed Secrets, Vault) over a plain `Secret`.

### Running a service outside Docker (e.g. from Visual Studio)

The `appsettings*.json` files intentionally contain no connection strings or credentials. To run one service directly, supply them yourself, for example:

```bash
cd src/Services/ShopEasy.OrderService
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Database=shopeasy_orders;Username=shopeasy;Password=<POSTGRES_PASSWORD>"
dotnet user-secrets set "RabbitMQ:Username" "<RABBITMQ_USER>"
dotnet user-secrets set "RabbitMQ:Password" "<RABBITMQ_PASSWORD>"
```

(and point `RabbitMQ:Host`, `IdentityServer:Authority`, `Redis:Connection` at `localhost` or the published container ports.)

### Security notes

- **Monitoring pages and APIs need a login.** `/outbox`, `/sagas` and `/deadletters` on the gateway redirect to the login page, and `/api/outbox/*` on the Order service requires the `admin` role. Swagger UIs and metrics ports are still open in this project; put them behind your network controls in production.
- **IdentityServer signing keys** are generated at runtime and stored in a persistent volume (`identity-keys` in Compose, a PVC in Kubernetes) so tokens survive restarts. They are no longer committed to git. Running several Identity replicas needs shared key storage (database or a key vault), which is on the roadmap.
- **Secrets that were committed in the early history** of this repository (database password, client secrets, a signing key) must be treated as compromised: rotate them anywhere they were used.
- **Never enable** `SEED_DEMO_USERS`, `EXPOSE_ACCESS_TOKEN` or `GRAFANA_ANONYMOUS_ENABLED` on anything reachable from the internet.

---

## How to Use It

### 1. Log in

Open `http://localhost:5000` and log in. Demo users are only created when `SEED_DEMO_USERS=true` in your `.env`:

```
alice@shopeasy.com  → admin     (password = SEED_ADMIN_PASSWORD)
bob@shopeasy.com    → customer  (password = SEED_CUSTOMER_PASSWORD)
```

In production leave seeding off and create users through your own onboarding flow.

### 2. Place an order

From the browser console at `localhost:5000` (while logged in):

```javascript
fetch('/api/orders', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    items: [{
      productId: '00000000-0000-0000-0000-000000000005',
      quantity:  1
    }],
    notes: 'my first order'
  })
}).then(r => r.json()).then(console.log)
```

You only send **what** to buy. The product name and price (`89.99` for the seeded "4K Webcam", which has 100 in stock) are filled in by the server from the Product service. Any `productName` / `unitPrice` you add to an item is ignored. See the [Order API](#order-api) for all rules and error responses.

### 3. Watch the saga play out

Open the **Saga Monitor** at `http://localhost:5000/sagas`. Within a couple of seconds you'll see the order move through:

```
Started → Reserving → Payment → Completed
```

The Payment column shows `Succeeded`. Inventory quantity drops by 1.

### 4. List your orders (CQRS read model)

```javascript
fetch('/api/orders').then(r => r.json()).then(console.log)
```

This reads from the denormalized `OrderSummaries` table — fast, no joins.

---

## Order API

All endpoints require a logged-in user (cookie via the gateway, or a Bearer token directly against the Order service).

| Method & path | Description |
|---|---|
| `POST /api/orders` | Place an order. Rate limited to 10 per minute per user. |
| `GET /api/orders?status=&page=&pageSize=` | List the current user's orders (CQRS read model). `pageSize` is clamped to 1-50. |
| `GET /api/orders/{orderId}` | One of the current user's orders. |
| `GET /api/orders/status-counts` | Order counts per status for the current user. |
| `DELETE /api/orders/{id}` | Cancel an order (owner or admin) through the saga. See [Cancelling an order](#cancelling-an-order). |

### `POST /api/orders`

```json
{
  "items": [
    { "productId": "00000000-0000-0000-0000-000000000005", "quantity": 2 }
  ],
  "notes": "leave at the door"
}
```

| Field | Rules |
|---|---|
| `items` | Required, 1 to **50** entries. |
| `items[].productId` | Required GUID of an existing, active product. |
| `items[].quantity` | Whole number from 1 to **100**. |
| `items[].productName`, `items[].unitPrice` | **Ignored.** Accepted only so older clients keep working. The server uses the Product service's name and price. |
| `notes` | Optional, at most **500** characters (trimmed). |

The same `productId` appearing in several items is **merged into one line** (quantities added); the merged quantity must still be at most 100.

**How an order is processed:** validate the request, look up every product in the Product service (name, price, active flag), check stock (Redis/gRPC, see pattern 11), then save the order and its outbox event in one transaction and return `201`. The saga then reserves stock and takes payment asynchronously.

| Status | Meaning |
|---|---|
| `201 Created` | Order accepted (status `Pending`). The saga then reserves stock, takes payment and commits the stock asynchronously; the order becomes `Confirmed` once all of that succeeded, or `Cancelled` if any step failed (stock short, payment declined, ...). The body contains the order with the **server-side** prices and `totalAmount`. |
| `400 Bad Request` | Invalid request, unknown product, inactive product, or insufficient stock. Body: `{ "message": "Order could not be placed.", "errors": ["..."] }` with every problem listed. |
| `401 Unauthorized` | Not logged in / invalid token. |
| `429 Too Many Requests` | Order rate limit exceeded. Through the gateway the response includes a `Retry-After` header. |
| `503 Service Unavailable` | The Product service could not be reached or answered in time, so the price cannot be trusted. **The order is rejected, nothing is saved.** The response has `Retry-After: 5`; retrying is safe. |

Example `400`:

```json
{
  "message": "Order could not be placed.",
  "errors": [
    "Item 1: quantity must be at least 1.",
    "Product 3f2c0c1e-0000-0000-0000-000000000099 does not exist."
  ]
}
```

---

### Cancelling an order

`DELETE /api/orders/{id}` does not just flip a status. It hands the order to the saga, which releases the reserved stock and refunds a payment that still arrives, using the same compensation steps as a failed order. The owner or an admin can cancel; for anyone else the order looks like it does not exist (`404`).

| Where the order is when you cancel | What happens | Response |
|---|---|---|
| Saga not created yet (the outbox event is still in flight) | A cancelled saga is recorded, so the saga starter never reserves stock or takes a payment for it | `204 No Content`, order `Cancelled` |
| `Started` (nothing reserved or charged yet) | Cancelled on the spot | `204 No Content`, order `Cancelled` |
| `ReservingInventory` or `ProcessingPayment` | Saga moves to `ReleasingInventory` and sends `ReleaseInventoryCommand`. A payment that still succeeds is refunded (late-payment guard), a reservation that arrives late is released again. When Inventory confirms the release the order becomes `Cancelled` | `202 Accepted` `{ "status": "Cancelling", ... }`; poll `GET /api/orders/{id}` until `Cancelled` |
| `ReleasingInventory` (already being cancelled, e.g. after a payment failure) | Nothing new to do | `202 Accepted` |
| Already `Cancelled` / `Failed` | Nothing to do (repeating a cancel is safe) | `204 No Content` |
| `Completing` or `Completed` (payment taken, order confirmed or being finalized) | Not cancellable: there is no return/refund flow after completion | `409 Conflict` `{ "message": "..." }` |

The reason is stored on the saga and shown as the order's `failureReason` (`Cancelled by customer` / `Cancelled by admin`). If the release command is lost, the saga timeout service re-sends it (idempotent) up to 3 times before flagging the order for manual intervention.

---

## Inventory Stock Model

Each product has two numbers in the Inventory database:

| Field | Meaning |
|---|---|
| `Quantity` | Units physically in stock. Only decreases when an order is **committed** (or an admin adjusts stock). |
| `ReservedQty` | Units held for orders that are still in flight. |
| `AvailableQuantity` | `Quantity − ReservedQty` — what a new order can still reserve. |

Every change is also written to the `StockTransactions` ledger. For an order the ledger holds up to three kinds of rows, recognised by their exact `Reason` text (`Data/StockReasons.cs`): *Reserved for Order …*, *Committed for Order …*, *Released reservation for Order …*.

**Lifecycle of one order's stock**

```
reserve  → ReservedQty += n                      (Quantity unchanged)
   ├── commit  (payment succeeded) → Quantity −= n, ReservedQty −= n   → InventoryCommittedEvent
   │           └── impossible (no reservation / already released)      → InventoryCommitFailedEvent → saga refunds + cancels
   └── release (payment failed / timeout)       → ReservedQty −= n     → InventoryReleasedEvent
```

**Concurrency — no overselling.** Reserve, commit, release and the admin *adjust* endpoint all run in a database transaction and first lock the affected product rows with `SELECT … FOR UPDATE`, always in `ProductId` order (so two operations touching several products cannot deadlock). Two Inventory instances therefore cannot both read "1 left" and both reserve it: the second waits for the first, then sees the updated stock. The locking lives behind `IInventoryLocking` (`Data/InventoryLocking.cs`).

**Idempotency — safe to run twice.** The state checks happen *under* those locks, so even two copies of a message processed at the same moment cannot both act:

| Command | Duplicate / odd case | Result |
|---|---|---|
| Reserve | Order already reserved | No second reservation; `InventoryReservedEvent` re-sent |
| Reserve | Not enough stock / unknown product / quantity ≤ 0 | Nothing reserved; `InventoryReservationFailedEvent` |
| Reserve | Same product on several lines | Reserved once with the total |
| Commit | Already committed | No second deduction; `InventoryCommittedEvent` re-sent |
| Commit | Reservation was released, or never existed | `InventoryCommitFailedEvent` |
| Release | Already released | No second release; `InventoryReleasedEvent` re-sent |
| Release | Already **committed** | Refused (stock is already deducted); nothing published, error logged |
| Release | Nothing was reserved | `InventoryReleasedEvent` (nothing to undo) so the saga can finish |
| Release | A reserved product has no inventory record (a data inconsistency that should never happen) | Logged as an error; the other products are released, the release is still recorded for that product (so a repeat is a no-op) and `InventoryReleasedEvent` is published so the order still gets cancelled |

Transient failures (database unavailable, ...) are *not* reported as events: the message is retried and ends up in the `*_error` queue if it keeps failing (see Dead Letter Queues).

**Messages**

| Message | Direction | Queue / consumer |
|---|---|---|
| `ReserveInventoryCommand`, `CommitInventoryCommand`, `ReleaseInventoryCommand` | Order → Inventory | `inventory-reserve`, `inventory-commit`, `inventory-release` |
| `InventoryReservedEvent`, `InventoryReservationFailedEvent`, `InventoryReleasedEvent` | Inventory → Order | `order-saga-inventory-reserved`, `-failed`, `-released` |
| `InventoryCommittedEvent`, `InventoryCommitFailedEvent` | Inventory → Order | `order-saga-inventory-committed`, `order-saga-inventory-commit-failed` |

### Checking the concurrency behaviour yourself

Unit tests cover the logic (idempotency, state checks, quantity maths) with an in-memory database, but they cannot prove the row locks — that needs a real PostgreSQL (planned: Testcontainers). To see it with the running stack, log in as the admin at `http://localhost:5000` and paste this into the browser console. It leaves exactly 5 *Standing Desks* in stock and then places 10 orders for one desk each, all at the same moment (the order rate limit is 10 per minute, so run it once a minute):

```javascript
const id = '00000000-0000-0000-0000-000000000003';          // Standing Desk
const json = { 'Content-Type': 'application/json' };

// 1. set stock to exactly 5
const inv = await (await fetch(`/api/inventory/${id}`)).json();
await fetch(`/api/inventory/${id}/adjust`, { method: 'POST', headers: json,
  body: JSON.stringify({ quantityChange: 5 - inv.quantity, reason: 'concurrency test' }) });

// 2. 10 simultaneous orders for 1 desk each
const statuses = await Promise.all(Array.from({ length: 10 }, () =>
  fetch('/api/orders', { method: 'POST', headers: json,
    body: JSON.stringify({ items: [{ productId: id, quantity: 1 }] }) }).then(r => r.status)));
console.log('HTTP statuses', statuses);

// 3. wait for the sagas to finish, then look at the result
await new Promise(r => setTimeout(r, 20000));
console.log('stock', await (await fetch(`/api/inventory/${id}`)).json());
const orders = await (await fetch('/api/orders?pageSize=50')).json();
console.log(orders.items.filter(o => o.itemsSummary.includes('Standing')).map(o => o.statusName));
```

Expected: **at most 5 orders end up `Confirmed`**, the rest `Cancelled` (their `failureReason` says "Insufficient stock…"), and the stock ends at `quantity: 0, reservedQty: 0` — never negative, never more than 5 sold. (Some orders may already be rejected with `400` at acceptance time, based on the cached stock level; that is fine.) Payments succeed in the default Compose setup because `Testing__ForcePaymentSuccess` is on for the Payment service.

---

## Developer Portal & Monitoring

All reachable from the landing page at `http://localhost:5000`:

| Tool | URL | Credentials |
|---|---|---|
| **Developer Portal** | http://localhost:5000 | — |
| Outbox Monitor | http://localhost:5000/outbox | log in as an admin |
| **Saga Monitor** | http://localhost:5000/sagas | log in as an admin |
| Dead Letter Monitor | http://localhost:5000/deadletters | log in as an admin |
| RabbitMQ Management | http://localhost:15672 | `RABBITMQ_USER` / `RABBITMQ_PASSWORD` from `.env` |
| Jaeger (tracing) | http://localhost:16686 | — |
| Prometheus | http://localhost:9090 | — |
| Grafana (dashboards) | http://localhost:3000 | `admin` / `GRAFANA_ADMIN_PASSWORD` from `.env` |
| Seq (logs) | http://localhost:5341 | — |
| Redis Insight | http://localhost:5540 | — |
| pgAdmin | http://localhost:5050 | `admin@shopeasy.com` / `PGADMIN_PASSWORD` from `.env` |
| Service Swagger | http://localhost:5101–5104 | — |

**pgAdmin connection** (to inspect databases): host `postgres`, port `5432`, user `shopeasy`, password = `POSTGRES_PASSWORD` from your `.env`.

### Working with dead letters

A message that fails all its retries is parked in an `<endpoint>_error` queue (for example `payment-process_error`). The Order service logs `DEAD LETTER ALERT: Queue … has N failed message(s)` every 30 seconds while a queue is non-empty.

1. Open http://localhost:5000/deadletters (as an admin), or call `GET /api/outbox/deadletters`, to see every error queue and its message count.
2. Find the cause — the Seq logs of the failing service, or the message and its `MT-Fault-*` headers in the RabbitMQ management UI.
3. Fix it, then click **Requeue** (or `POST /api/outbox/deadletters/{queue}/requeue`). Each message is copied back to its original queue, confirmed by the broker, and only then removed from the error queue, so a crash can duplicate a message but never lose one — and every consumer is idempotent.

| Response | Meaning |
|---|---|
| `200` `{ moved: n }` | `n` messages were moved back. |
| `400` | Not one of the known error queues (live queues and unknown names are refused). |
| `404` | That error queue does not exist — nothing has failed there. |
| `503` | RabbitMQ is not reachable; retry shortly. |

### Testing failure scenarios

The Saga Monitor is the best way to see the system's resilience. Try this:

```bash
# 1. Place an order, then immediately stop the payment service
docker-compose stop payment

# 2. Wait ~2 minutes — the saga times out, releases inventory, and cancels.
#    The Saga Monitor shows the order Cancelled with reason "Timed out waiting for payment".

# 3. Bring payment back
docker-compose start payment

# 4. The late payment is processed, the zombie guard detects the cancelled saga,
#    and issues a refund. The Payment column now shows "Refunded 💜".
```

To reliably force a successful payment for the refund test, set `Testing__ForcePaymentSuccess: "true"` on the payment service in `docker-compose.yml`.

---

## Testing

```bash
# Unit tests — need nothing but the .NET 8 SDK
dotnet test src/Tests/ShopEasy.UnitTests

# Integration tests (run with the stack up)
dotnet test src/Tests/ShopEasy.IntegrationTests
```

**Unit tests — 142 test cases.** 122 run anywhere (they use the EF Core InMemory provider with a fresh database per test); the other 20 need a real PostgreSQL or RabbitMQ and are **skipped automatically** unless the environment variables below are set, so `dotnet test` is green on a machine without them.

| Area | Where | What it covers |
|---|---|---|
| Order cancellation | `Sagas/CancellationTests` | Cancelling at every saga stage, who may cancel, a lost release command, and what the saga handlers do after a cancel (release confirmed, late payment refunded, late reservation released, timeout retries). |
| Commit flow | `Sagas/CommitFlowTests` | Completion only after `InventoryCommittedEvent`, refund on `InventoryCommitFailedEvent`, duplicate or late events never refund, cancel or release a finished order. |
| Saga starter, orchestrator, outbox entries | `Sagas/SagaOrchestratorTests`, `Consumers/*` | Starting a saga (including idempotency), the inventory-reserved consumer, outbox entry state. |
| Inventory consumers | `Inventory/*` | Reserve, commit and release including duplicates, out-of-order messages, shortages, multi-product orders and a missing inventory record. |
| Order placement | `Orders/*` | Request validation and limits, server-side price/name resolution, the Product service client (404, 5xx, network failure, malformed response). |
| Messaging (no servers needed) | `Messaging/*` | The broker readiness gate (including against MassTransit's real health check registration), outbox delivery pausing while the bus is not ready, the dead-letter monitor and the requeue endpoint's responses. |

The larger saga state-machine suite (`Sagas/SagaStateTransitionTests.cs`) is currently commented out and needs to be updated for the current consumer constructors; the payment-succeeded/failed consumer tests in `SagaOrchestratorTests.cs` are likewise disabled.

### Tests that need PostgreSQL and RabbitMQ

What these check cannot be faked: row locks, transactions, the MassTransit outbox and AMQP queue operations. Every test creates and drops its own database / queues.

| Test class | What it proves |
|---|---|
| `Infrastructure/InventoryPostgresTests` | 12 orders of 3 units racing for 10 units reserve at most 3 times (the real `SELECT … FOR UPDATE`); the same command delivered 4 times at once reserves once; a consumer that publishes and then fails sends **nothing** and rolls everything back; `BeginOrJoinAsync` joins an open transaction and leaves the commit to its owner. |
| `Infrastructure/PaymentPostgresTests` | A payment command through the outbox saves the payment and delivers the event; the same command twice charges once and still confirms. |
| `Infrastructure/OutboxDeliveryPostgresTests` | Ready bus → entries published and marked delivered; not ready → nothing published and no retry spent; a failing entry is retried, parked after 5 attempts and no longer blocks the others; deliveries earlier in a batch stay delivered when a later one fails. |
| `Infrastructure/ErrorQueueClientTests` | Counting and moving messages between real queues over AMQP (body, headers and message id preserved), missing source / destination, broker unreachable. |

Start throw-away servers and point the tests at them. They were verified against PostgreSQL 16 and RabbitMQ 3.12, with the RabbitMQ management plugin deliberately **not** enabled — the application does not need it:

```bash
docker run -d --name shopeasy-test-pg -e POSTGRES_PASSWORD=test -p 55432:5432 postgres:16-alpine
docker run -d --name shopeasy-test-mq -e RABBITMQ_DEFAULT_USER=test -e RABBITMQ_DEFAULT_PASS=test -p 55672:5672 rabbitmq:3.13-alpine

export SHOPEASY_TEST_POSTGRES="Host=localhost;Port=55432;Username=postgres;Password=test"
export SHOPEASY_TEST_RABBITMQ="amqp://test:test@localhost:55672"
dotnet test src/Tests/ShopEasy.UnitTests          # now 142 run, 0 skipped

docker rm -f shopeasy-test-pg shopeasy-test-mq
```

`SHOPEASY_TEST_POSTGRES` is a server connection string **without** a database name (the role needs `CREATEDB`). Not covered yet by automated tests: the saga row locking (`ISagaLocking`) — it needs the same kind of PostgreSQL test and is planned with the CI work (roadmap step 7).

---

## Horizontal Scaling

The Order service is stateless and built to scale. The distributed outbox lock means duplicate instances never double-process messages.

```bash
# Scale to 3 instances
docker-compose up --build --scale order=3

# Scale up live, no downtime
docker-compose up --scale order=5 --no-recreate

# Scale back down
docker-compose up --scale order=2 --no-recreate
```

The gateway needs **no changes** — Docker's internal DNS round-robins across all instances behind `http://order:8080`. To verify only one instance claims each batch:

```bash
docker-compose logs order | findstr "acquired lock"
```

---

## Kubernetes Deployment

Full manifests live in `k8s/`. They deploy the same Docker images with production orchestration: auto-scaling, self-healing, rolling updates, and resource limits.

### Quick start (Docker Desktop Kubernetes)

```bash
# 1. Enable Kubernetes in Docker Desktop → Settings → Kubernetes

# 2. Build all service images locally
docker build -t shopeasy-identity:latest  -f src/Identity/ShopEasy.Identity/Dockerfile .
docker build -t shopeasy-product:latest   -f src/Services/ShopEasy.ProductService/Dockerfile .
docker build -t shopeasy-inventory:latest -f src/Services/ShopEasy.InventoryService/Dockerfile .
docker build -t shopeasy-payment:latest   -f src/Services/ShopEasy.PaymentService/Dockerfile .
docker build -t shopeasy-order:latest     -f src/Services/ShopEasy.OrderService/Dockerfile .
docker build -t shopeasy-gateway:latest   -f src/Gateway/ShopEasy.Gateway/Dockerfile .

# 3. Apply infrastructure first
kubectl apply -f k8s/namespace/
#    Create your real secrets from the template (secrets.yaml is git-ignored):
cp k8s/secrets/secrets.example.yaml k8s/secrets/secrets.yaml   # then replace every CHANGE_ME
kubectl apply -f k8s/secrets/secrets.yaml
kubectl apply -f k8s/configmaps/
kubectl apply -f k8s/postgres/
kubectl apply -f k8s/rabbitmq/
kubectl apply -f k8s/redis/

# 4. Apply services
kubectl apply -f k8s/identity/
kubectl apply -f k8s/product/
kubectl apply -f k8s/inventory/
kubectl apply -f k8s/payment/
kubectl apply -f k8s/order/
kubectl apply -f k8s/gateway/

# 5. Watch them come up
kubectl get pods -n shopeasy -w
```

Gateway is exposed on NodePort `30000` (`http://localhost:30000`), Identity on `30001`.

### What Kubernetes adds over Compose

| Docker Compose | Kubernetes |
|---|---|
| Manual restart | Self-healing (auto-restart crashed pods) |
| Manual `--scale` | HorizontalPodAutoscaler (CPU-based, 2–10 pods) |
| Single machine | Multi-node |
| Restart = downtime | Zero-downtime rolling updates |
| No resource caps | Enforced CPU/memory requests & limits |

### Tear down

```bash
kubectl delete namespace shopeasy
```

> **Note on migrations under multiple replicas:** the Order service swallows the `42P07` ("relation already exists") error so that when two replicas start simultaneously, the one that loses the migration race continues cleanly instead of crash-looping.

---

## Project Structure

```
ShopEasy/
├── docker-compose.yml
├── .env.example                   # copy to .env (git-ignored) — all secrets live there
├── postgres-init.sql              # creates the 5 databases
├── prometheus.yml
├── grafana/provisioning/          # auto-provisioned dashboards
├── k8s/                           # Kubernetes manifests
│   ├── namespace/  secrets/  configmaps/
│   ├── postgres/  rabbitmq/  redis/
│   └── identity/ product/ inventory/ payment/ order/ gateway/
├── src/
│   ├── Identity/ShopEasy.Identity/
│   ├── Gateway/ShopEasy.Gateway/
│   ├── Shared/ShopEasy.Shared/    # contracts, middleware, cache, protos
│   │   ├── Contracts/             # events & commands
│   │   ├── Protos/inventory.proto
│   │   ├── Services/CacheService.cs
│   │   └── Middleware/CorrelationIdMiddleware.cs
│   └── Services/
│       ├── ShopEasy.ProductService/
│       ├── ShopEasy.OrderService/
│       │   ├── Sagas/             # orchestrator + consumers
│       │   ├── Services/          # outbox delivery + broker readiness, error-queue (AMQP) client,
│       │   │                      #   dead-letter monitor, timeout, gRPC client, inventory cache,
│       │   │                      #   product catalog client, order request validation/pricing
│       │   ├── ReadModels/        # CQRS OrderSummary
│       │   └── Controllers/       # Commands + Queries (CQRS split)
│       ├── ShopEasy.InventoryService/
│       │   ├── Grpc/              # gRPC service implementation
│       │   ├── Data/              # DbContext, StockReasons (ledger kinds), InventoryLocking (FOR UPDATE),
│       │   │                      #   UnitOfWork (join the MassTransit outbox transaction)
│       │   └── Consumers/         # reserve / commit / release
│       └── ShopEasy.PaymentService/
│           ├── Consumers/         # process / refund
│           └── Data/              # DbContext, UnitOfWork (join the MassTransit outbox transaction)
└── src/Tests/
    ├── ShopEasy.UnitTests/        # unit tests + Infrastructure/ (needs PostgreSQL / RabbitMQ, see Testing)
    └── ShopEasy.IntegrationTests/
```

---

## Troubleshooting

**Currency shows as `¤` instead of `$`** — cosmetic locale issue in the container; ignore.

**Prometheus targets DOWN** — ensure `prometheus.yml` and the Grafana provisioning files exist on disk *before* `docker-compose up` (Docker mounts files; a missing file becomes a directory).

**gRPC fails with `HTTP_1_1_REQUIRED`** — Inventory must expose gRPC on a dedicated HTTP/2-only port (8090). REST stays on 8080. The env var `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2UNENCRYPTEDSUPPORT=1` enables cleartext HTTP/2.

**Order migration crash on multi-instance start** — handled in code (swallows `42P07`); if you see it, ensure you rebuilt the Order image.

**`docker-compose --scale` fails with "container name" warning** — the Order service must not have a fixed `container_name` and must expose a dynamic port (`- "8080"`, not `- "5102:8080"`).

**`docker-compose up` fails with "Set X in .env"** — a required variable is missing from your `.env`. Compare it with `.env.example`.

**Services cannot log in to Postgres after you edited `.env`** — Postgres only applies `POSTGRES_PASSWORD` when it first creates the data volume. Restore the old password, or wipe local data with `docker-compose down -v`.

**`/sagas`, `/outbox` or `/deadletters` redirect to a login page** — expected: they require a login (as an admin to see data). Seed demo users with `SEED_DEMO_USERS=true` in `.env`.

**`/account/token` returns 404 with a message** — it is disabled unless `EXPOSE_ACCESS_TOKEN=true` in `.env` (local development only). Set it, then run `docker-compose up -d gateway` to recreate the gateway container with the new value, and open `/account/token` again while logged in.

**`/account/me` shows `userId: null` or empty `roles`** — your login cookie was created by an older gateway build. Sign out at `/account/logout` and log in again so the session picks up the `sub` and `role` claims. (The services themselves always read the user and role from the access token, so API calls were not affected; the visible effects were this endpoint and the gateway's per-user / admin rate limits.)

**`429 Too Many Requests` when listing or cancelling orders** — only placing an order (`POST /api/orders`) has the strict 10-per-minute limit; other order calls use the normal per-user limit of 100 per minute. If you still see `429`, you are exceeding that, or you are running an older gateway image: `docker-compose up -d --build gateway`.

**`POST /api/orders` returns 503 "Product catalog is temporarily unavailable"** — the Order service could not get a price from the Product service (down, slow, or its circuit breaker is open). Check `docker-compose ps product` and its logs, then retry; nothing was saved.

**`POST /api/orders` returns 400 "Product ... does not exist" / "no longer available"** — the `productId` is not in the Product catalog, or the product was deactivated. List valid ids with `GET /api/v1/products`.

**`DELETE /api/orders/{id}` returns `409`** — the payment was already taken and the order is completing or completed; cancelling after that is not supported yet. **It returns `202`** — the cancellation was accepted and the saga is releasing stock; the order shows `Cancelled` shortly after (check `/sagas`, or the `inventory-release` queue if it takes longer than a couple of minutes).

**An order stays `Pending` and the saga sits in `Completing`** — payment succeeded and the saga is waiting for the Inventory service to confirm the stock commit. Check that the `inventory` container is up and that `inventory-commit` / `inventory-commit_error` in the RabbitMQ UI are empty. The timeout service re-sends the commit every ~2 minutes; the commit is idempotent, so this is safe.

**An order was `Cancelled` and refunded although payment had succeeded** — the Inventory service reported `InventoryCommitFailedEvent`: the reservation had been released (for example by a timeout) before the commit arrived. The saga ends in `Failed`, the order is cancelled and the payment is refunded. The reason is on the saga (`/sagas`) and in the order's `failureReason`.

**Orders stay `Pending` and the Order log says `Outbox: message bus is not ready — delivery paused`** — the Order service cannot publish because RabbitMQ is unreachable or one of its queues is not declared yet. Entries stay in the outbox (check `/outbox`) and are delivered automatically, in order, once the log says `message bus is ready — delivery enabled`; nothing needs to be done unless it persists, in which case check the `rabbitmq` container and the `RabbitMQ__*` settings.

**Dead letter page shows nothing / `503 RabbitMQ is not reachable`** — the Order service reaches RabbitMQ over AMQP (port 5672), not through the management UI. Check `RabbitMQ__Host`, `RabbitMQ__Port`, the credentials and that the `rabbitmq` container is healthy. The management UI (15672) being disabled does not affect it.

**Payment or Inventory processed a command but the next step never happened** — the result event is stored in the service's `OutboxMessage` table until it is delivered. `SELECT count(*) FROM "OutboxMessage";` in `shopeasy_payments` / `shopeasy_inventory` should be 0; rows that stay mean RabbitMQ was unreachable at that moment. They are delivered when the broker is back and the command is redelivered — the stock/payment change itself is not repeated. If the command ended up in an `_error` queue instead, requeue it from the Dead Letter Monitor page; the saga timeout service also re-sends commands that never got an answer.

**Redis down crashes EF migrations** — the Redis connection string uses `abortConnect=false` and the multiplexer is registered so startup doesn't depend on Redis being up.

---

## Production Hardening Roadmap & Known Limitations

This project is being hardened step by step. Status:

| # | Step | Status |
|---|---|---|
| 1 | Repo hygiene: `.gitignore`, `.dockerignore`, no build output / IDE files / keys in git | Done |
| 2 | Secrets out of the repo (`.env`, k8s Secret template), monitoring endpoints behind auth, no hardcoded credentials | Done |
| 3 | Server-side pricing and request validation on `POST /api/orders` | Done |
| 4 | Inventory correctness: row locking on reserve/commit/release/adjust, idempotent commit and release, saga waits for the commit result (refunds if the commit is impossible) | Done |
| 5 | Order cancellation through the saga (release stock, refund a late payment), plus row locking on every saga transition | Done |
| 6 | Replace RabbitMQ management-API polling with a bus-ready check and AMQP dead-letter tooling; MassTransit transactional inbox/outbox for Payment/Inventory events | Done |
| 7 | Observability fixes (per-service log names, correlation across messages, MassTransit/EF tracing), CI (GitHub Actions, running the PostgreSQL/RabbitMQ infrastructure tests against service containers) with saga row-locking tests, re-enable the commented-out saga tests | Planned |
| 8 | Dockerfile (non-root, slim image) and Kubernetes hardening (pinned image tags, shared Identity key storage, network policies) | Planned |

**Known limitations today** (documented so nobody is surprised):

- Orders cannot be cancelled once the payment is taken (`Completing` / `Completed`): there is no return, restock or partial-refund flow yet.
- If the saga orchestrator itself fails three times on a saga it marks the saga `Failed` but does not update the order, which stays `Pending` until the customer cancels it (cancelling a `Failed` saga fixes the order).
- The saga row locking (`ISagaLocking`) is not covered by automated tests yet; the unit tests use a stand-in. (The Inventory row locking is, against a real PostgreSQL — see *Testing*.)
- The Order service's saga handlers still publish their commands *after* committing (they do not use the MassTransit outbox). A crash in that gap is recovered by the saga timeout service, which re-sends the command; every consumer is idempotent.
- The Inventory consumers invalidate the Redis stock cache while MassTransit's transaction is still open (the commit follows immediately after the consumer returns). In that few-millisecond window a reader can re-cache the old stock level for up to its 30-second TTL. The cache is advisory — the saga's reservation is the authoritative check.
- A message published to an exchange that has no queue bound yet is dropped by the broker, so a command sent before its consuming service has started for the first time is lost until the saga timeout re-sends it. (Docker Compose and Kubernetes start order makes this rare.)
- The MassTransit inbox only remembers a message for its 30-minute duplicate-detection window; beyond that, duplicates are caught by the business-level checks (pattern 7).
- Inventory endpoints process one message at a time per instance (`ConcurrentMessageLimit = 1`). Correctness no longer depends on it (row locks do), so it can be raised for throughput.
- `InventoryDbContext` has no unique database constraint on the ledger; idempotency relies on the row locks.
- Migrations run at service start-up; with several replicas the race is only handled for the Order service.
- Swagger UIs and Prometheus metrics ports are unauthenticated.

---

## License

MIT
