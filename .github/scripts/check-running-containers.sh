#!/usr/bin/env bash
# After `docker compose up --wait`: the six application containers must really be running
# unprivileged. The Dockerfile says USER and Compose says read_only, but only a running container
# proves the combination works (the process starts, the health check passes, and it is not root).
set -euo pipefail

failed=0
for service in identity gateway product inventory payment order; do
  container="$(docker compose ps -q "$service")"
  if [ -z "$container" ]; then
    echo "::error::service '$service' is not running"; failed=1; continue
  fi

  uid="$(docker exec "$container" id -u)"
  read_only="$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$container")"
  new_privileges="$(docker inspect -f '{{json .HostConfig.SecurityOpt}}' "$container")"

  echo "$service: uid=$uid read-only-rootfs=$read_only security-opt=$new_privileges"

  [ "$uid" != "0" ]                 || { echo "::error::$service runs as root"; failed=1; }
  [ "$read_only" = "true" ]         || { echo "::error::$service has a writable root file system"; failed=1; }
  [[ "$new_privileges" == *no-new-privileges* ]] || { echo "::error::$service may gain privileges"; failed=1; }
done
exit "$failed"
