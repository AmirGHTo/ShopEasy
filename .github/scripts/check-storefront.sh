#!/usr/bin/env bash
# After `docker compose up --wait`: the gateway serves the built storefront at /, answers the pages
# of the app with it, caches its files as intended, never answers an unknown API path with HTML, and
# sends the security headers (a strict Content-Security-Policy the built app has to satisfy).
#
# Every address is fetched once, and its headers are read from that same answer. The storefront's
# files and page shell are not rate limited; only /portal counts against the anonymous limit, which
# the end-to-end test that runs right after this script shares.
set -euo pipefail

base=http://localhost:5000
failed=0
fail() { echo "::error::$1"; failed=1; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# fetch <name> <path>: one GET; the body and the headers are kept under <name>; prints the status
fetch() {
  curl --silent --show-error --dump-header "$tmp/$1.headers" --output "$tmp/$1.body" --write-out '%{http_code}' "$base$2"
}
# header <name> <header>: the value of one response header of that answer
header() { tr -d '\r' < "$tmp/$1.headers" | grep -i "^$2:" | head -n 1 | cut -d: -f2- | sed 's/^ *//' || true; }
body() { cat "$tmp/$1.body"; }

# ── The storefront is served ──────────────────────────────────────────────────────────────────────
[ "$(fetch root /)" = "200" ] && body root | grep -q 'id="root"' || fail "/ does not serve the storefront"

[ "$(fetch deep /products/123)" = "200" ] && body deep | grep -q 'id="root"' \
  || fail "a page address of the app (/products/123) does not get the app"

asset="$(body root | grep -oE '/assets/[^"]+\.js' | head -n 1 || true)"
if [ -z "$asset" ]; then
  fail "index.html names no script under /assets"
else
  curl --fail --silent --show-error --head "$base$asset" | grep -qi 'cache-control: public, max-age=31536000, immutable' \
    || fail "$asset is not cached for good"
fi

[ "$(header root cache-control)" = "no-cache" ] || fail "index.html is not checked on every visit"

[ "$(fetch api /api/does-not-exist)" = "404" ] || fail "an unknown API path does not answer 404"

[ "$(fetch portal /portal)" = "200" ] && body portal | grep -q 'Dev Portal' \
  || fail "the developer portal is not at /portal"

# ── Security headers: what a browser is told about each kind of answer ────────────────────────────
for name in root deep api portal; do
  [ "$(header $name x-content-type-options)" = "nosniff" ] || fail "the $name answer is sent without X-Content-Type-Options: nosniff"
  [ "$(header $name x-frame-options)" = "DENY" ] || fail "the $name answer is sent without X-Frame-Options: DENY"
  header $name content-security-policy | grep -q "frame-ancestors 'none'" || fail "the $name answer may be framed (Content-Security-Policy)"
done

storefront_csp="$(header deep content-security-policy)"
grep -q "script-src 'self';" <<< "$storefront_csp" || fail "the storefront may run scripts from more than its own files: $storefront_csp"
if grep -qE "unsafe-inline|unsafe-eval" <<< "$storefront_csp"; then fail "the storefront policy allows inline code or eval: $storefront_csp"; fi

[ "$(header api content-security-policy)" = "default-src 'none'; frame-ancestors 'none'" ] \
  || fail "an API answer is not sent with the policy that allows nothing"
header portal content-security-policy | grep -q "script-src 'self' 'unsafe-inline'" \
  || fail "the operator pages (inline scripts) are not sent with their own policy"

# The strict policy only works because the build has no inline code: a <script> without src, a
# <style> element, a style="" attribute or an on...="" handler in index.html would be blocked in a
# browser. Fail here, not in front of a customer.
if body root | grep -oiE '<script[^>]*>' | grep -viq 'src='; then fail "index.html has an inline <script>"; fi
if body root | grep -qiE '<style|[[:space:]]style=|[[:space:]]on[a-z]+=' ; then fail "index.html has inline styles or event handlers"; fi

[ "$failed" = "0" ] && echo "The gateway serves the storefront, with its security headers"
exit "$failed"
