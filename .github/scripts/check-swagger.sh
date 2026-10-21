#!/usr/bin/env bash
# After `docker compose up --wait`: the Swagger UI of an API follows Swagger__Enabled. The Compose
# stack has it on by default; recreating Product with SWAGGER_ENABLED=false must switch it off.
set -euo pipefail

json=http://localhost:5101/swagger/v1/swagger.json
page=http://localhost:5101/
status() { curl --silent --output /dev/null --write-out '%{http_code}' --max-time 10 "$1"; }

on="$(status "$json")"
echo "Swagger enabled (the Compose default): $json -> $on"
[ "$on" = "200" ] || { echo "::error::Swagger is not served although it is enabled"; exit 1; }

SWAGGER_ENABLED=false docker compose up -d --no-deps --wait --wait-timeout 300 product
off_json="$(status "$json")"
off_page="$(status "$page")"
echo "Swagger disabled: $json -> $off_json, $page -> $off_page"
if [ "$off_json" = "200" ] || [ "$off_page" = "200" ]; then
  echo "::error::Swagger is served although it is disabled"; exit 1
fi
