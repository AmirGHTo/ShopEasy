#!/usr/bin/env bash
# Writes a dump of every ShopEasy database into a directory: one file per database, in PostgreSQL's
# "custom" format (compressed, and restorable one database at a time with ops/restore.sh).
#
#   bash ops/backup.sh [directory]        default: backups/<UTC date and time>
#
# It runs pg_dump inside the postgres container, so the version always matches the server. The
# service can keep running: pg_dump reads a consistent snapshot of each database. The snapshots of
# the five databases are taken one after the other, a few seconds apart, not at one instant.
#
# PG_EXEC says how to reach the PostgreSQL tools (default: the Compose container). For Kubernetes:
#   PG_EXEC="kubectl -n shopeasy exec -i deploy/postgres --" bash ops/backup.sh
# For a PostgreSQL on this machine (reached with the usual PGHOST / PGPORT / PGPASSWORD variables):
#   PG_EXEC="" bash ops/backup.sh
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PG_EXEC=${PG_EXEC-docker compose -f $ROOT/docker-compose.yml exec -T postgres}
PG_USER=${PG_USER:-shopeasy}
# Orders before Inventory and Payment: the saga writes to them after Order, so in a restore they are never
# older than Order (an order a step behind is settled by its saga timeout; one ahead of its payment could not be)
DATABASES=${DATABASES:-shopeasy_identity shopeasy_products shopeasy_orders shopeasy_inventory shopeasy_payments}

directory=${1:-backups/$(date -u +%Y%m%dT%H%M%SZ)}
mkdir -p "$directory"
trap 'rm -f "$directory"/*.partial' EXIT

for database in $DATABASES; do
  file="$directory/$database.dump"
  # Written under another name and renamed at the end: a dump that stopped half way must never be
  # mistaken for a good one.
  $PG_EXEC pg_dump --username "$PG_USER" --format custom "$database" > "$file.partial"
  # Reading the whole archive back (the SQL it would run is thrown away) proves that the file is
  # complete: a truncated file has an intact table of contents but stops in the middle of the data
  $PG_EXEC pg_restore --file /dev/null < "$file.partial"
  mv "$file.partial" "$file"
  echo "$database: $(wc -c < "$file") bytes"
done

echo "Backup written to $directory"
