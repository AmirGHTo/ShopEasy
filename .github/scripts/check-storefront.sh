#!/usr/bin/env bash
# After `docker compose up --wait`: the gateway serves the built storefront at /, answers the pages
# of the app with it, caches its files as intended, and never answers an unknown API path with HTML.
set -euo pipefail

base=http://localhost:5000
failed=0
fail() { echo "::error::$1"; failed=1; }

root="$(curl --fail --silent --show-error "$base/")"
grep -q 'id="root"' <<< "$root" || fail "/ does not serve the storefront"

curl --fail --silent --show-error "$base/products/123" | grep -q 'id="root"' \
  || fail "a page address of the app (/products/123) does not get the app"

asset="$(grep -oE '/assets/[^"]+\.js' <<< "$root" | head -n 1 || true)"
if [ -z "$asset" ]; then
  fail "index.html names no script under /assets"
else
  curl --fail --silent --show-error --head "$base$asset" | grep -qi 'cache-control: public, max-age=31536000, immutable' \
    || fail "$asset is not cached for good"
fi

curl --silent --show-error --head "$base/" | grep -qi 'cache-control: no-cache' \
  || fail "index.html is not checked on every visit"

status="$(curl --silent --output /dev/null --write-out '%{http_code}' "$base/api/does-not-exist")"
[ "$status" = "404" ] || fail "an unknown API path answers $status instead of 404"

curl --fail --silent --show-error "$base/portal" | grep -q 'Dev Portal' \
  || fail "the developer portal is not at /portal"

[ "$failed" = "0" ] && echo "The gateway serves the storefront"
exit "$failed"
