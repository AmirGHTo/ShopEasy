#!/usr/bin/env python3
"""Every alert must tell a person what to do, and Alertmanager must only name alerts that exist.

`promtool` checks that the rules are valid and fire when they should. This checks what promtool does not:

  * every alert has a severity (critical, warning or none), a summary and a runbook_url;
  * the runbook_url points at a heading that really is in the README ("Runbooks": one heading per
    alert), so a mail never links to a page that does not exist;
  * alertmanager.yml names only alerts that exist (an inhibition written for a renamed alert would
    silently stop doing anything).

Usage:  python3 .github/scripts/check-alert-rules.py      (from the repository root)
"""
import re
import sys

import yaml

SEVERITIES = {"critical", "warning", "none"}
errors: list[str] = []

rules = yaml.safe_load(open("prometheus-rules.yml", encoding="utf-8"))
alerts = {}
for group in rules["groups"]:
    for rule in group["rules"]:
        if "alert" not in rule:
            continue
        name = rule["alert"]
        if name in alerts:
            errors.append(f"{name}: defined twice")
        alerts[name] = rule

headings = set()
for line in open("README.md", encoding="utf-8"):
    match = re.match(r"^#{1,6}\s+(.*?)\s*$", line)
    if match:
        headings.add(re.sub(r"[^\w\s-]", "", match.group(1).lower()).replace(" ", "-"))

for name, rule in sorted(alerts.items()):
    severity = rule.get("labels", {}).get("severity")
    if severity not in SEVERITIES:
        errors.append(f"{name}: severity must be one of {sorted(SEVERITIES)}, found {severity!r}")
    annotations = rule.get("annotations", {})
    if not annotations.get("summary"):
        errors.append(f"{name}: needs a summary annotation")
    url = annotations.get("runbook_url")
    if not url:
        errors.append(f"{name}: needs a runbook_url annotation")
    elif "#" not in url or url.split("#", 1)[1] not in headings:
        errors.append(f"{name}: runbook_url {url} points at a README heading that does not exist "
                      f"(add '#### {name}' to the Runbooks section)")

config = yaml.safe_load(open("alertmanager.yml", encoding="utf-8"))
named = set()


def collect_route(route):
    for matcher in route.get("matchers", []):
        named.update(re.findall(r'alertname\s*=~?\s*"([^"]+)"', matcher))
    for child in route.get("routes", []):
        collect_route(child)


collect_route(config["route"])
for rule in config.get("inhibit_rules", []):
    for matcher in rule.get("source_matchers", []) + rule.get("target_matchers", []):
        named.update(re.findall(r'alertname\s*=~?\s*"([^"]+)"', matcher))

for reference in sorted(named):
    for part in reference.split("|"):
        if part not in alerts:
            errors.append(f"alertmanager.yml names the alert '{part}', which does not exist in prometheus-rules.yml")

if errors:
    for message in errors:
        print(f"::error::{message}")
    sys.exit(1)

print(f"OK: {len(alerts)} alerts, each with a severity, a summary and a runbook heading in the README; "
      f"alertmanager.yml names {len(named)} alert reference(s), all existing")
