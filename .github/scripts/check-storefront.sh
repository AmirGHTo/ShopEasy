#!/usr/bin/env bash
# After `docker compose up --wait`: the gateway serves the built storefront at /, answers the pages
# of the app with it, caches its files as intended, never answers an unknown API path with HTML, and
# sends the security headers (a strict Content-Security-Policy the built app has to satisfy).
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

# ── Security headers: what a browser is told about each kind of answer ────────────────────────────
header() { # header <path> <name>: the value of one response header of a GET
  curl --silent --show-error --dump-header - --output /dev/null "$base$1" | tr -d '\r' \
    | grep -i "^$2:" | head -n 1 | cut -d: -f2- | sed 's/^ *//' || true
}

for path in / /products /api/v1/products /portal; do
  [ "$(header "$path" x-content-type-options)" = "nosniff" ] || fail "$path is sent without X-Content-Type-Options: nosniff"
  [ "$(header "$path" x-frame-options)" = "DENY" ] || fail "$path is sent without X-Frame-Options: DENY"
  header "$path" content-security-policy | grep -q "frame-ancestors 'none'" || fail "$path may be framed (Content-Security-Policy)"
done

storefront_csp="$(header /products content-security-policy)"
grep -q "script-src 'self';" <<< "$storefront_csp" || fail "the storefront may run scripts from more than its own files: $storefront_csp"
if grep -qE "unsafe-inline|unsafe-eval" <<< "$storefront_csp"; then fail "the storefront policy allows inline code or eval: $storefront_csp"; fi

[ "$(header /api/v1/products content-security-policy)" = "default-src 'none'; frame-ancestors 'none'" ] \
  || fail "an API answer is not sent with the policy that allows nothing"
header /portal content-security-policy | grep -q "script-src 'self' 'unsafe-inline'" \
  || fail "the operator pages (inline scripts) are not sent with their own policy"

# The strict policy only works because the build has no inline code: a <script> without src, a
# <style> element, a style="" attribute or an on...="" handler in index.html would be blocked in a
# browser. Fail here, not in front of a customer.
if grep -oiE '<script[^>]*>' <<< "$root" | grep -viq 'src='; then fail "index.html has an inline <script>"; fi
if grep -qiE '<style|[[:space:]]style=|[[:space:]]on[a-z]+=' <<< "$root"; then fail "index.html has inline styles or event handlers"; fi

[ "$failed" = "0" ] && echo "The gateway serves the storefront, with its security headers"
exit "$failed"
