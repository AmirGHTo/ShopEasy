# ShopEasy — Production-Grade Microservices E-Commerce Platform

A complete, production-hardened e-commerce backend built with **.NET 8** and **C#**, demonstrating the full spectrum of microservices patterns: distributed transactions, event-driven messaging, CQRS, resilience, observability, and container orchestration.

This project is built for learning and interview preparation, but every pattern is implemented to production standards — not toy examples.

---

## Table of Contents

1. [What This Is](#what-this-is)
2. [Architecture Overview](#architecture-overview)
3. [The Services](#the-services)
4. [Technology Stack](#technology-stack)
5. [Patterns & Mechanisms Explained](#patterns--mechanisms-explained)
6. [Getting Started](#getting-started)
7. [How to Use It](#how-to-use-it)
8. [Developer Portal & Monitoring](#developer-portal--monitoring)
9. [Testing](#testing)
10. [Horizontal Scaling](#horizontal-scaling)
11. [Kubernetes Deployment](#kubernetes-deployment)
12. [Project Structure](#project-structure)
13. [Troubleshooting](#troubleshooting)
14. [Interview Talking Points](#interview-talking-points)

---

## What This Is

ShopEasy is an online store backend split into independent microservices. A customer can browse products, place an order, and have that order automatically validated, paid for, and fulfilled — with full failure recovery if anything goes wrong along the way.

The interesting part is **what happens behind a single "Place Order" click**: a distributed transaction spans four services (Order, Inventory, Payment, and back to Order), coordinated by a saga, with automatic compensation, refunds, and timeout handling if any step fails.

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

- **Synchronous** — Gateway → services over HTTP; Order → Inventory over gRPC (fast, typed, binary).
- **Asynchronous** — services publish/consume events through RabbitMQ. This is how the saga orchestrates the order lifecycle without services being tightly coupled.

---

## The Services

| Service | Port | Responsibility |
|---|---|---|
| **Identity** | 5001 | OAuth2 / OpenID Connect provider (Duende IdentityServer). Issues JWTs. |
| **Gateway** | 5000 | Single entry point. YARP reverse proxy + Backend-for-Frontend. Handles auth, rate limiting, routing, and hosts the developer monitoring portal. |
| **Product** | 5101 | Product catalog CRUD. API-versioned (v1/v2). Redis-cached. |
| **Order** | 5102 | Order placement and orchestration. Owns the saga, outbox, and CQRS read model. Horizontally scalable. |
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
OrderConfirmed → ReserveInventory → ProcessPayment → CommitInventory → Completed
```

**Saga states:** `Started → ReservingInventory → ProcessingPayment → Completing → Completed`
(or the compensation branch: `→ ReleasingInventory → Cancelled/Failed`)

**Compensation matrix — every failure has a defined recovery:**

| Failure point | Inventory | Payment | Recovery action |
|---|---|---|---|
| Stock check fails | not reserved | not charged | Cancel order |
| Payment declined | reserved | not charged | Release inventory → Cancel |
| Timeout waiting for payment | reserved | not charged | Release inventory → Cancel |
| Late payment after cancel | released | charged | **Refund** → stay Cancelled |
| Commit fails after payment | reserved | charged | Retry commit (never auto-fail) |

### 5. Outbox Pattern

When an order is saved, the event that starts the saga is written to an **outbox table in the same database transaction** as the order itself. A background service then publishes outbox entries to RabbitMQ.

**Why:** it guarantees that "order saved" and "event published" are atomic. If RabbitMQ is down when the order is placed, the event still gets delivered once the broker recovers — no lost orders, no phantom events.

### 6. Distributed Outbox Locking

When the Order service runs as **multiple instances**, they'd all try to publish the same outbox messages. We use PostgreSQL's `SELECT ... FOR UPDATE SKIP LOCKED` so each message is claimed by exactly one instance; others skip locked rows. The database row lock *is* the distributed mutex — no external coordination service (ZooKeeper, etcd) needed.

### 7. Idempotent Consumers

Every message consumer checks whether it has already processed a given event (via a `ProcessedEvents` table or a saga-state guard) before acting. RabbitMQ guarantees *at-least-once* delivery, meaning duplicates happen. Idempotency makes reprocessing harmless.

### 8. Zombie Message Guard

A subtle distributed-systems bug: a payment command sent *before* a saga timed out can arrive *after* the saga was already cancelled. Without a guard, this "zombie" message would complete a cancelled order. Each consumer re-reads the saga state inside a locked transaction and, if the saga is already terminal, issues a compensating action (refund / release) instead of proceeding.

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
- **Layer 5** — the saga's `ReserveInventory` does the *real* check with `SELECT FOR UPDATE` (always strong)

This decouples order acceptance from the Inventory service being online, while the saga still prevents overselling. This is the industry-standard approach for high-volume commodity e-commerce.

### 12. gRPC Internal Communication

Order → Inventory stock checks use gRPC instead of REST: a binary, strongly-typed contract (`inventory.proto`) with a single batch call for multiple products. ~3× faster than JSON/HTTP and the contract is compile-time enforced. REST stays available on a separate port for external clients.

### 13. Resilience (Polly)

HTTP clients use Polly pipelines: exponential-backoff retries and circuit breakers. MassTransit consumers use message retry with exponential backoff before sending to dead-letter queues.

### 14. Dead Letter Queues

Messages that fail repeatedly land in `*_error` queues. A `DeadLetterMonitor` background service polls these via the RabbitMQ management API, and the developer portal exposes them with a requeue endpoint.

### 15. Rate Limiting

The gateway enforces tiered limits: anonymous 30/min per IP, authenticated 100/min per user (admin 500), order placement 10/min per user. Returns `429` with `Retry-After`.

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
      productId:   '00000000-0000-0000-0000-000000000005',
      productName: '4K Webcam',
      quantity:    1,
      unitPrice:   89.99
    }],
    notes: 'my first order'
  })
}).then(r => r.json()).then(console.log)
```

Seeded product `...0005` is "4K Webcam" with quantity 100.

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
# Unit tests (saga state machine, compensation, timeouts, idempotency, outbox)
dotnet test tests/ShopEasy.UnitTests

# Integration tests (run with the stack up)
dotnet test tests/ShopEasy.IntegrationTests
```

**Unit test coverage (31 tests):** every saga state transition, the full compensation path, the zombie-message guard, idempotency guards, timeout handling for every stuck state, and edge cases (non-existent saga, zero-amount order). Tests use the EF Core InMemory provider with a fresh database per test for isolation.

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
│       │   ├── Services/          # outbox, timeout, gRPC client, inventory cache
│       │   ├── ReadModels/        # CQRS OrderSummary
│       │   └── Controllers/       # Commands + Queries (CQRS split)
│       ├── ShopEasy.InventoryService/
│       │   ├── Grpc/              # gRPC service implementation
│       │   └── Consumers/         # reserve / commit / release
│       └── ShopEasy.PaymentService/
│           └── Consumers/         # process / refund
└── tests/
    ├── ShopEasy.UnitTests/
    └── ShopEasy.IntegrationTests/
```

---

## Troubleshooting

**Currency shows as `¤` instead of `$`** — cosmetic locale issue in the container; ignore.

**Prometheus targets DOWN** — ensure `prometheus.yml` and the Grafana provisioning files exist on disk *before* `docker-compose up` (Docker mounts files; a missing file becomes a directory).

**gRPC fails with `HTTP_1_1_REQUIRED`** — Inventory must expose gRPC on a dedicated HTTP/2-only port (8090). REST stays on 8080. The env var `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2UNENCRYPTEDSUPPORT=1` enables cleartext HTTP/2.

**Order migration crash on multi-instance start** — handled in code (swallows `42P07`); if you see it, ensure you rebuilt the Order image.

**`docker-compose --scale` fails with "container name" warning** — the Order service must not have a fixed `container_name` and must expose a dynamic port (`- "8080"`, not `- "5102:8080"`).

**Redis down crashes EF migrations** — the Redis connection string uses `abortConnect=false` and the multiplexer is registered so startup doesn't depend on Redis being up.

---

## Interview Talking Points

A quick map of "which pattern solves which problem," useful for explaining the project:

- **"How do you handle a transaction across services?"** → Saga (orchestration) with per-state compensation.
- **"How do you guarantee an event is published when the DB write succeeds?"** → Outbox pattern (event written in the same transaction).
- **"How do you avoid double-publishing when scaled out?"** → `SELECT FOR UPDATE SKIP LOCKED`.
- **"What about duplicate messages?"** → Idempotent consumers + processed-event tracking.
- **"What if a service dies mid-saga?"** → Timeout service detects stuck sagas and compensates.
- **"What if a payment arrives after you already cancelled?"** → Zombie-message guard issues a refund.
- **"How do you make reads fast?"** → CQRS denormalized read model.
- **"Strong or eventual consistency for stock?"** → Hybrid: cached fast-path + saga's `SELECT FOR UPDATE` for the authoritative check.
- **"How do you debug a request across services?"** → Correlation ID + Jaeger traces + Seq logs.
- **"How do you scale?"** → Stateless Order service, distributed outbox lock, Docker DNS load-balancing / Kubernetes HPA.

---

## License

MIT — use it freely for learning and reference.

---

*Built as a deep-dive into production microservices patterns with .NET 8, Docker, and Kubernetes.*