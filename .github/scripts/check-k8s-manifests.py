#!/usr/bin/env python3
"""Rules the rendered Kubernetes manifests (`kubectl kustomize k8s`) must follow.

kubeconform only checks that the YAML is *valid*; these are the deployment rules that make it
*safe*, so that a later edit cannot quietly undo them:

  * the six service images come from the registry and are pinned to a commit (sha-<hex>), never
    `latest`, never a local name, never `imagePullPolicy: Never` (the early samples ran locally
    built images that nobody could reproduce);
  * their containers run as non-root, cannot gain privileges, have a read-only root file system and
    no Linux capabilities;
  * they take the shared ConfigMap (`envFrom`), because the settings an operator is told to change
    there (cookie security, forwarded headers, sign-up, ...) only reach a pod that reads it;
  * third-party images carry an explicit version tag (not `latest`);
  * the namespace denies ingress by default.

Usage:  kubectl kustomize k8s | python3 .github/scripts/check-k8s-manifests.py
"""
import re
import sys

import yaml

APPS = {"identity", "gateway", "product", "inventory", "payment", "order"}
PINNED = re.compile(r"ghcr\.io/[a-z0-9._-]+/shopeasy-[a-z]+:sha-[0-9a-f]{7,40}")

errors: list[str] = []
seen_apps: set[str] = set()
default_deny = False

for doc in yaml.safe_load_all(sys.stdin):
    if not doc:
        continue
    kind, name = doc.get("kind"), doc.get("metadata", {}).get("name")

    if kind == "NetworkPolicy" and name == "default-deny-ingress":
        spec = doc["spec"]
        default_deny = spec.get("podSelector") == {} and "Ingress" in spec.get("policyTypes", [])

    if kind != "Deployment":
        continue

    pod = doc["spec"]["template"]["spec"]
    for container in pod["containers"]:
        where = f"{name}/{container['name']}"
        image = container["image"]

        if name in APPS:
            seen_apps.add(name)
            if not PINNED.fullmatch(image):
                errors.append(f"{where}: image '{image}' must be ghcr.io/<owner>/shopeasy-<service>:sha-<commit>")
            if container.get("imagePullPolicy") == "Never":
                errors.append(f"{where}: imagePullPolicy: Never only works with locally built images")

            pod_security = pod.get("securityContext", {})
            security = container.get("securityContext", {})
            if not (pod_security.get("runAsNonRoot") or security.get("runAsNonRoot")):
                errors.append(f"{where}: must set runAsNonRoot: true")
            if security.get("allowPrivilegeEscalation") is not False:
                errors.append(f"{where}: must set allowPrivilegeEscalation: false")
            if security.get("readOnlyRootFilesystem") is not True:
                errors.append(f"{where}: must set readOnlyRootFilesystem: true")
            if "ALL" not in security.get("capabilities", {}).get("drop", []):
                errors.append(f"{where}: must drop ALL capabilities")
            if "limits" not in container.get("resources", {}):
                errors.append(f"{where}: needs resource limits")
            if not any(ref.get("configMapRef", {}).get("name") == "shopeasy-config" for ref in container.get("envFrom", [])):
                errors.append(f"{where}: must take the shared ConfigMap (envFrom: configMapRef: shopeasy-config)")
        elif ":" not in image.rsplit("/", 1)[-1] or image.endswith(":latest"):
            errors.append(f"{where}: third-party image '{image}' needs an explicit version tag")

for missing in sorted(APPS - seen_apps):
    errors.append(f"no Deployment for service '{missing}' in the rendered manifests")
if not default_deny:
    errors.append("NetworkPolicy 'default-deny-ingress' (podSelector {}, Ingress) is missing")

if errors:
    for message in errors:
        print(f"::error::{message}")
    sys.exit(1)

print(f"OK: {len(seen_apps)} service Deployments pinned, non-root, read-only, on the shared ConfigMap; ingress denied by default")
