#!/usr/bin/env python3
"""What Grafana and Jaeger are provisioned with is well-formed and agrees with itself.

Grafana reads its provisioning files only when it starts, and says little when one is wrong. This checks
what can be checked without starting it (the running check is check-grafana.sh):

  * every data source has a unique uid, and a uid that another data source refers to (the exemplar link
    of Prometheus to Jaeger) exists;
  * every dashboard is JSON, has a unique uid when it names one, and its panels that ask for exemplars
    query a Prometheus data source;
  * jaeger/ui.json is JSON and every link pattern has a type, a url and a text.

Usage:  python3 .github/scripts/check-grafana-provisioning.py      (from the repository root)
"""
import glob
import json
import sys

import yaml

errors: list[str] = []

# ── data sources ─────────────────────────────────────────────────────────────
sources = {}
for path in sorted(glob.glob("grafana/provisioning/datasources/*.yml")):
    document = yaml.safe_load(open(path, encoding="utf-8"))
    for source in document.get("datasources", []):
        name, uid = source.get("name"), source.get("uid")
        if not name or not uid:
            errors.append(f"{path}: a data source needs a name and a uid (found {name!r}, {uid!r})")
            continue
        if uid in sources:
            errors.append(f"{path}: the uid '{uid}' is used twice")
        sources[uid] = source

for uid, source in sources.items():
    for destination in (source.get("jsonData") or {}).get("exemplarTraceIdDestinations", []):
        target = destination.get("datasourceUid")
        if target and target not in sources:
            errors.append(f"data source '{uid}': an exemplar link points at the data source '{target}', which is not provisioned")
        if not target and not destination.get("url"):
            errors.append(f"data source '{uid}': an exemplar destination needs a datasourceUid or a url")

# ── dashboards ───────────────────────────────────────────────────────────────
dashboard_uids = {}
for path in sorted(glob.glob("grafana/provisioning/dashboards/*.json")):
    try:
        dashboard = json.load(open(path, encoding="utf-8"))
    except ValueError as error:
        errors.append(f"{path}: not valid JSON ({error})")
        continue
    uid = dashboard.get("uid")
    if uid:
        if uid in dashboard_uids:
            errors.append(f"{path}: the dashboard uid '{uid}' is also used by {dashboard_uids[uid]}")
        dashboard_uids[uid] = path
    for panel in dashboard.get("panels", []):
        for target in panel.get("targets", []):
            if target.get("exemplar"):
                datasource = (target.get("datasource") or panel.get("datasource") or {})
                kind = datasource.get("type") if isinstance(datasource, dict) else None
                default_is_prometheus = any(s.get("isDefault") and s.get("type") == "prometheus" for s in sources.values())
                if kind != "prometheus" and not (kind is None and default_is_prometheus):
                    errors.append(f"{path}: panel '{panel.get('title')}' asks for exemplars but does not query Prometheus")

# ── Jaeger's UI configuration ────────────────────────────────────────────────
try:
    ui = json.load(open("jaeger/ui.json", encoding="utf-8"))
    for pattern in ui.get("linkPatterns", []):
        for key in ("type", "url", "text"):
            if not pattern.get(key):
                errors.append(f"jaeger/ui.json: a link pattern needs '{key}'")
except (OSError, ValueError) as error:
    errors.append(f"jaeger/ui.json: {error}")

if errors:
    for message in errors:
        print(f"::error::{message}")
    sys.exit(1)

print(f"OK: {len(sources)} data sources, {len(glob.glob('grafana/provisioning/dashboards/*.json'))} dashboard file(s) "
      "and Jaeger's UI configuration are well-formed and agree with each other")
