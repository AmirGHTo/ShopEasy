#!/usr/bin/env bash
# Causes one of the failures the alerts are written for, and prints what to watch and where.
#   ops/demo/break.sh payment | postgres | rabbitmq | order | product-hang | poison
# Undo it with ops/demo/fix.sh <scenario>. README, "Traffic and failures to try".
set -euo pipefail
# shellcheck source=ops/demo/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
run_scenario break "${1:-}"
