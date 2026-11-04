#!/usr/bin/env bash
# Undoes what ops/demo/break.sh did.
#   ops/demo/fix.sh payment | postgres | rabbitmq | order | product-hang | poison
set -euo pipefail
# shellcheck source=ops/demo/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
run_scenario fix "${1:-}"
