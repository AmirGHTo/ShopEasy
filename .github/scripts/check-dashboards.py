#!/usr/bin/env python3
"""The provisioned dashboards are well-formed, explain themselves, and ask for metrics that exist.

A dashboard panel that queries a metric name nobody exports does not fail: it shows "No data", and a person
reads that as "all quiet". This catches it before that.

  static     (no stack needed)
    * every dashboard file is JSON, has a title and the tag "shopeasy" (the links between dashboards
      are made by tag), and unique panel ids;
    * every panel has a title and a description (what a bad value means, which runbook to open) and a
      query in every target;
    * every shopeasy_* metric a query or a variable names is one the code of the repository defines (its
      histogram _bucket/_sum/_count series count as the metric), and every shopeasy:* name is a
      recording rule of prometheus-rules.yml.

  live       (the Compose stack is up and the end-to-end tests have run)
    * every query is accepted by Prometheus (a typo in a function or a bracket is a 400 there);
    * every other metric the dashboards name (http_*, process_*, rabbitmq_*, pg_*, redis_*, ...) exists in
      Prometheus;
    * with a Grafana address: Grafana lists every dashboard of the repository by its tag, and loads each
      one with all its panels.

Standard library and PyYAML. Usage:
  check-dashboards.py static
  check-dashboards.py live <prometheus-url> [<grafana-url>]      (Grafana's password: GRAFANA_ADMIN_PASSWORD or .env)
"""
import base64
import glob
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

import yaml

DASHBOARDS = sorted(glob.glob("grafana/provisioning/dashboards/*.json"))
errors: list[str] = []

# PromQL words that look like metric names but are not
NOT_METRICS = {"and", "or", "unless", "bool", "offset", "by", "without", "on", "ignoring", "group_left",
               "group_right", "inf", "nan", "sum", "min", "max", "avg", "count", "stddev", "stdvar", "topk",
               "bottomk", "quantile", "count_values", "group"}


def metric_names(expression: str) -> set[str]:
    """The metric names a PromQL expression (or a label_values(...) variable query) selects."""
    text = expression
    inner = re.match(r"^\s*label_values\((.*)\)\s*$", text, re.S)
    if inner:  # label_values(metric{...}, label): the metric is the part before the last comma
        text = inner.group(1).rsplit(",", 1)[0] if "," in inner.group(1) else ""
    text = re.sub(r'"(?:[^"\\]|\\.)*"', '""', text)                 # string literals
    text = re.sub(r"\$\{?[A-Za-z_][A-Za-z0-9_]*\}?", "", text)       # Grafana variables
    while re.search(r"\{[^{}]*\}", text):                            # label matchers
        text = re.sub(r"\{[^{}]*\}", "", text)
    text = re.sub(r"\[[^\]]*\]", "", text)                           # ranges
    text = re.sub(r"\b(?:by|without|on|ignoring|group_left|group_right)\s*\([^)]*\)", "", text)
    names = set()
    for match in re.finditer(r"[A-Za-z_:][A-Za-z0-9_:]*", text):
        name = match.group(0)
        rest = text[match.end():].lstrip()
        if name in NOT_METRICS or rest.startswith("("):              # a keyword, or a function call
            continue
        names.add(name)
    return names


def panels_of(dashboard: dict):
    for panel in dashboard.get("panels", []):
        yield panel
        yield from panels_of(panel)  # panels inside a collapsed row


def queries_of(dashboard: dict):
    """(where, expression) for every target and every query variable."""
    for panel in panels_of(dashboard):
        for target in panel.get("targets", []):
            yield f"panel '{panel.get('title')}'", target.get("expr", "")
    for variable in dashboard.get("templating", {}).get("list", []):
        query = variable.get("query")
        query = query.get("query") if isinstance(query, dict) else query
        if variable.get("type") == "query" and query:
            yield f"variable '{variable.get('name')}'", query


def known_metrics() -> tuple[set[str], set[str]]:
    """shopeasy_* names the code defines, and the recording rules of prometheus-rules.yml."""
    code = set()
    for path in glob.glob("src/**/*.cs", recursive=True):
        if "/obj/" in path or "/bin/" in path or "/Tests/" in path:
            continue
        code.update(re.findall(r'"(shopeasy_[a-z0-9_]+)"', open(path, encoding="utf-8-sig").read()))
    recorded = set()
    for group in yaml.safe_load(open("prometheus-rules.yml", encoding="utf-8"))["groups"]:
        recorded.update(rule["record"] for rule in group["rules"] if "record" in rule)
    return code, recorded


def load():
    dashboards = {}
    for path in DASHBOARDS:
        try:
            dashboards[path] = json.load(open(path, encoding="utf-8"))
        except ValueError as error:
            errors.append(f"{path}: not valid JSON ({error})")
    return dashboards


def static(dashboards):
    code, recorded = known_metrics()
    titles, uids = {}, {}
    for path, dashboard in dashboards.items():
        name = os.path.basename(path)
        title = dashboard.get("title")
        if not title:
            errors.append(f"{name}: the dashboard needs a title")
        elif title in titles:
            errors.append(f"{name}: the title '{title}' is also used by {titles[title]}")
        titles[title] = name
        if dashboard.get("uid"):
            if dashboard["uid"] in uids:
                errors.append(f"{name}: the uid '{dashboard['uid']}' is also used by {uids[dashboard['uid']]}")
            uids[dashboard["uid"]] = name
        if "shopeasy" not in dashboard.get("tags", []):
            errors.append(f"{name}: needs the tag 'shopeasy' (the dashboards link to each other by it)")

        ids = [p.get("id") for p in panels_of(dashboard)]
        if len(ids) != len(set(ids)):
            errors.append(f"{name}: panel ids are not unique")
        for panel in panels_of(dashboard):
            if panel.get("type") == "row":
                continue
            where = f"{name}: panel '{panel.get('title') or panel.get('id')}'"
            if not panel.get("title"):
                errors.append(f"{where}: needs a title")
            if not (panel.get("description") or "").strip():
                errors.append(f"{where}: needs a description (what a bad value means, which runbook to open)")
            if not panel.get("targets"):
                errors.append(f"{where}: has no query")
            for target in panel.get("targets", []):
                if not (target.get("expr") or "").strip():
                    errors.append(f"{where}: a query is empty")

        for where, expression in queries_of(dashboard):
            for metric in sorted(metric_names(expression)):
                if metric.startswith("shopeasy:"):
                    if metric not in recorded:
                        errors.append(f"{name}: {where} names the recording rule '{metric}', which prometheus-rules.yml does not define")
                elif metric.startswith("shopeasy_"):
                    base = re.sub(r"_(bucket|sum|count)$", "", metric)
                    if metric not in code and base not in code:
                        errors.append(f"{name}: {where} names the metric '{metric}', which no code of the repository defines")
    return len(dashboards)


def get(url, headers=None):
    request = urllib.request.Request(url, headers=headers or {})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def live(dashboards, prometheus, grafana):
    existing = set(get(f"{prometheus}/api/v1/label/__name__/values")["data"])
    substitutions = [(r"\$\{?__rate_interval\}?", "5m"), (r"\$\{?__interval\}?", "1m"), (r"\$\{?__range\}?", "1h"),
                     (r"\$\{?[A-Za-z_][A-Za-z0-9_]*\}?", ".*")]
    checked = 0
    for path, dashboard in dashboards.items():
        name = os.path.basename(path)
        for where, expression in queries_of(dashboard):
            if expression.lstrip().startswith("label_values("):
                pass  # Grafana's own syntax: only its metric names are checked, below
            else:
                query = expression
                for pattern, replacement in substitutions:
                    query = re.sub(pattern, replacement, query)
                try:
                    get(f"{prometheus}/api/v1/query?" + urllib.parse.urlencode({"query": query}))
                    checked += 1
                except urllib.error.HTTPError as error:
                    detail = json.loads(error.read().decode("utf-8", "replace")).get("error", error.reason)
                    errors.append(f"{name}: {where}: Prometheus refuses the query ({detail}): {expression}")
            for metric in sorted(metric_names(expression)):
                if metric.startswith(("shopeasy_", "shopeasy:")):
                    continue  # defined by the code; many appear only once something has happened
                if metric not in existing:
                    errors.append(f"{name}: {where} names the metric '{metric}', which Prometheus does not hold")

    if grafana:
        password = os.environ.get("GRAFANA_ADMIN_PASSWORD") or next(
            (line.split("=", 1)[1].strip() for line in open(".env", encoding="utf-8")
             if line.startswith("GRAFANA_ADMIN_PASSWORD=")), "")
        auth = {"Authorization": "Basic " + base64.b64encode(f"admin:{password}".encode()).decode()}
        listed = {item["title"]: item["uid"] for item in get(f"{grafana}/api/search?type=dash-db&tag=shopeasy", auth)}
        for path, dashboard in dashboards.items():
            title = dashboard["title"]
            if title not in listed:
                errors.append(f"Grafana does not list the dashboard '{title}' ({os.path.basename(path)})")
                continue
            loaded = get(f"{grafana}/api/dashboards/uid/{listed[title]}", auth)["dashboard"]
            wanted = len([p for p in panels_of(dashboard) if p.get("type") != "row"])
            found = len([p for p in panels_of(loaded) if p.get("type") != "row"])
            if found != wanted:
                errors.append(f"Grafana loaded '{title}' with {found} panels, the file has {wanted}")
    return checked


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode not in ("static", "live"):
        print(__doc__)
        return 2
    dashboards = load()
    if not dashboards:
        errors.append("no dashboard files found in grafana/provisioning/dashboards")
    static(dashboards)
    detail = f"{len(dashboards)} dashboards, {sum(1 for d in dashboards.values() for _ in panels_of(d))} entries"
    if mode == "live" and not errors:
        if len(sys.argv) < 3:
            print("live needs the Prometheus address")
            return 2
        checked = live(dashboards, sys.argv[2].rstrip("/"), sys.argv[3].rstrip("/") if len(sys.argv) > 3 else None)
        detail += f"; {checked} queries accepted by Prometheus" + (", every dashboard loaded by Grafana" if len(sys.argv) > 3 else "")
    if errors:
        for message in errors:
            print(f"::error::{message}")
        return 1
    print(f"OK: {detail}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
