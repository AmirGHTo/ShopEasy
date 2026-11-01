#!/usr/bin/env python3
"""Proves the whole tracing chain on the running Compose stack, after the end-to-end tests:

    services --OTLP--> OpenTelemetry Collector --OTLP--> Jaeger

by asking the Jaeger API for what an order leaves behind. The tests placed orders through the gateway,
so Jaeger must hold at least ONE trace that is the whole story of an order:

  * spans of the gateway, the Order, Inventory and Payment services in the same trace,
  * the HTTP request, the outbox delivery, the saga orchestrator and the messages that follow (so the
    trace was carried over each place where work waits in a database table),
  * database spans (PostgreSQL), Redis spans and the gRPC call from Order to Inventory,
  * the order's id on the spans ("order.id", what one searches for in Jaeger),
  * what the trace says about the service that wrote it (version, environment, instance).

Spans reach Jaeger a few seconds after they end (the Collector batches them), and the saga is still
finishing when the tests end, so it keeps looking for a while before it gives up.
Standard library only. Usage: check-traces.py [jaeger-url] [seconds-to-wait]
"""
import json
import sys
import time
import urllib.parse
import urllib.request

JAEGER = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:16686").rstrip("/")
WAIT_SECONDS = int(sys.argv[2]) if len(sys.argv) > 2 else 120

SERVICES = ["ShopEasy.Gateway", "ShopEasy.OrderService", "ShopEasy.InventoryService", "ShopEasy.PaymentService"]
# what the whole story of an order contains, as (what, how to recognise it in a span's tags and name)
WANTED = [
    ("the order's id on a span (order.id)", lambda s: "order.id" in s["tags"]),
    ("the outbox delivery (outbox.deliver)", lambda s: s["name"] == "outbox.deliver"),
    ("the saga orchestrator (saga.orchestrate) with the saga's status", lambda s: s["name"] == "saga.orchestrate" and "saga.status" in s["tags"]),
    ("a PostgreSQL command", lambda s: s["tags"].get("db.system") == "postgresql"),
    ("a Redis command", lambda s: s["tags"].get("db.system") == "redis"),
    ("the gRPC call to Inventory", lambda s: "grpc" in (s["tags"].get("rpc.system"), s["tags"].get("rpc.system.name"))),
    ("a message consumed in Payment", lambda s: s["service"] == "ShopEasy.PaymentService" and s["name"].endswith(" receive")),
]


def get(path, **query):
    url = f"{JAEGER}{path}?{urllib.parse.urlencode(query)}"
    with urllib.request.urlopen(url, timeout=30) as response:
        return json.load(response)


def flatten(trace):
    """The spans of one Jaeger trace as {service, name, tags, process} dictionaries."""
    processes = trace["processes"]
    for span in trace["spans"]:
        process = processes[span["processID"]]
        yield {
            "service": process["serviceName"],
            "name": span["operationName"],
            "tags": {t["key"]: t["value"] for t in span["tags"]},
            "process": {t["key"]: t["value"] for t in process.get("tags", [])},
        }


def missing_from(trace):
    """What this trace lacks of an order's whole story (empty = it is one)."""
    spans = list(flatten(trace))
    missing = []

    present = {s["service"] for s in spans}
    for service in SERVICES:
        if service not in present:
            missing.append(f"spans of {service}")

    for what, test in WANTED:
        if not any(test(s) for s in spans):
            missing.append(what)

    for service in SERVICES:
        process = next((s["process"] for s in spans if s["service"] == service), None)
        if process is None:
            continue  # already reported above
        for key in ("service.version", "deployment.environment", "service.instance.id"):
            if not process.get(key):
                missing.append(f"{key} in the resource of {service}")
    return missing


def best_candidate():
    """The trace that comes closest, among the recent traces that contain a gateway span."""
    # the API takes a time window in microseconds since the epoch: the last hour
    end = int(time.time() * 1_000_000)
    start = end - 3600 * 1_000_000
    traces = get("/api/traces", service="ShopEasy.Gateway", start=start, end=end, limit=1000).get("data") or []
    best = None
    for trace in traces:
        missing = missing_from(trace)
        if best is None or len(missing) < len(best[1]):
            best = (trace, missing)
        if not missing:
            break
    return len(traces), best


def main():
    deadline = time.time() + WAIT_SECONDS
    seen, best = 0, None
    while True:
        try:
            seen, best = best_candidate()
            if best is not None and not best[1]:
                trace = best[0]
                spans = list(flatten(trace))
                services = sorted({s["service"] for s in spans})
                print(f"OK: trace {trace['traceID']} has {len(spans)} spans of {len(services)} services: {', '.join(services)}")
                order_ids = {s["tags"]["order.id"] for s in spans if "order.id" in s["tags"]}
                print(f"OK: it is the trace of order {', '.join(sorted(order_ids))} (search order.id in Jaeger)")
                return 0
        except Exception as error:  # Jaeger not up yet, or the Collector has not delivered anything yet
            print(f"waiting: {error}")
        if time.time() > deadline:
            break
        time.sleep(5)

    if best is None:
        print(f"::error::Jaeger holds no trace with a gateway span after {WAIT_SECONDS} s ({seen} traces looked at). "
              "Do the services reach the Collector (Telemetry__Endpoint) and does it reach Jaeger?")
    else:
        print(f"::error::None of the {seen} traces is the whole story of an order. The closest lacks:")
        for lacking in best[1]:
            print(f"::error::  - {lacking}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
