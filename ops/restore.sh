#!/usr/bin/env bash
# Replaces the content of every ShopEasy database with a backup made by ops/backup.sh.
#
#   bash ops/restore.sh <directory> [--yes]
#
# STOP THE SERVICES FIRST (docker compose stop identity product inventory payment order gateway):
# a service that keeps writing while its database is replaced would write into the old data's
# place. The script checks that every file is there and readable before it touches anything, restores each
# database in one transaction (a restore that fails leaves that database as it was), and asks you
# to type "restore" unless you pass --yes. The same PG_EXEC / PG_USER / DATABASES variables as
# ops/backup.sh apply.
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PG_EXEC=${PG_EXEC-docker compose -f $ROOT/docker-compose.yml exec -T postgres}
PG_USER=${PG_USER:-shopeasy}
DATABASES=${DATABASES:-shopeasy_identity shopeasy_products shopeasy_orders shopeasy_inventory shopeasy_payments}

directory=${1:?usage: restore.sh <backup directory> [--yes]}
confirmed=${2:-}

for database in $DATABASES; do
  [ -s "$directory/$database.dump" ] || { echo "Missing or empty: $directory/$database.dump" >&2; exit 1; }
  # a truncated or damaged file is found now (reading it all, the SQL thrown away), not after the
  # first databases were already replaced
  $PG_EXEC pg_restore --file /dev/null < "$directory/$database.dump" \
    || { echo "Not a readable backup: $directory/$database.dump" >&2; exit 1; }
done

if [ "$confirmed" != "--yes" ]; then
  echo "This replaces the data of: $DATABASES"
  read -r -p 'Type "restore" to continue: ' answer
  [ "$answer" = "restore" ] || { echo "Nothing was changed."; exit 1; }
fi

for database in $DATABASES; do
  # --clean --if-exists drops what is there before creating it (and works on an empty database)
  $PG_EXEC pg_restore --username "$PG_USER" --dbname "$database" \
    --clean --if-exists --no-owner --single-transaction < "$directory/$database.dump"
  echo "$database restored"
done

echo "Restored from $directory. Start the services again."
