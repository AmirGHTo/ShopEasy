#!/usr/bin/env python3
"""A point on the latency graph leads to a real trace.

The services record, with each request's duration, the id of the request's trace (an exemplar, in the
OpenMetrics text format Prometheus scrapes). Prometheus keeps them when started with
--enable-feature=exemplar-storage, and the "HTTP Response Time (p95)" panel of Grafana shows them as dots
that link to Jaeger. This asks Prometheus for the exemplars of the requests the end-to-end tests have
just made (health checks left out, as the panel does: they are not traced), and asks Jaeger for those
traces. At least one has to be there, and the share that is not is printed.

Spans reach Jaeger a few seconds after they end (the Collector batches them), so it keeps looking for a
while before it gives up. Standard library only.
Usage: check-exemplars.py [prometheus-url] [jaeger-url] [seconds-to-wait]
"""
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

PROMETHEUS = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:9090").rstrip("/")
JAEGER = (sys.argv[2] if len(sys.argv) > 2 else "http://localhost:16686").rstrip("/")
WAIT_SECONDS = int(sys.argv[3]) if len(sys.argv) > 3 else 120
# the query of the Grafana panel, without the aggregation: the exemplars belong to the buckets
QUERY = 'http_request_duration_seconds_bucket{endpoint!~"/health.*"}'


def get(url):
    with urllib.request.urlopen(url, timeout=30) as response:
        return json.load(response)


def exemplar_trace_ids():
    now = int(time.time())
    query = urllib.parse.urlencode({"query": QUERY, "start": now - 3600, "end": now + 5})
    found = {}
    for series in get(f"{PROMETHEUS}/api/v1/query_exemplars?{query}").get("data", []):
        service = series["seriesLabels"].get("service", "?")
        for exemplar in series["exemplars"]:
            trace_id = exemplar["labels"].get("trace_id")
            if trace_id:
                found[trace_id] = service
    return found


def in_jaeger(trace_id):
    try:
        return bool(get(f"{JAEGER}/api/traces/{trace_id}").get("data"))
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return False
        raise


def main():
    deadline = time.time() + WAIT_SECONDS
    exemplars, known = {}, {}
    while True:
        try:
            exemplars = exemplar_trace_ids()
            for trace_id in list(exemplars)[:60]:
                if trace_id not in known or not known[trace_id]:
                    known[trace_id] = in_jaeger(trace_id)
            if any(known.values()):
                found = sum(known.values())
                print(f"OK: {found} of the {len(known)} exemplars checked lead to a trace in Jaeger "
                      f"({len(exemplars)} exemplars in Prometheus, services: {', '.join(sorted(set(exemplars.values())))})")
                return 0
        except Exception as error:  # not up yet, or nothing delivered yet
            print(f"waiting: {error}")
        if time.time() > deadline:
            break
        time.sleep(5)

    if not exemplars:
        print(f"::error::Prometheus holds no exemplar for {QUERY} after {WAIT_SECONDS} s. Does it run with "
              "--enable-feature=exemplar-storage, and do the services answer the OpenMetrics format "
              "(prometheus-net does when the scraper asks for it)?")
    else:
        print(f"::error::None of the {len(known)} exemplars checked is a trace in Jaeger. "
              "Are the services' traces sampled (Telemetry__SamplingRatio) and exported?")
    return 1


if __name__ == "__main__":
    sys.exit(main())
