#!/usr/bin/env bash
# Disaster rehearsal for the Compose stack, run by CI after the end-to-end tests have left data in
# every database:
#
#   1. stop the services (so nothing changes) and count the rows of every table
#   2. back the databases up with ops/backup.sh
#   3. lose the data: remove the PostgreSQL container and its volume, start a new, empty PostgreSQL
#   4. restore with ops/restore.sh
#   5. count again: every table must hold exactly the rows it held
#   6. start the services on the restored data (CI then places an order through them)
#
# A backup nobody has restored is a hope, not a backup; this is the proof that the two scripts work
# on the real databases, and the time it prints is the measured part of the recovery time.
set -euo pipefail

apps="identity product inventory payment order gateway"
databases="shopeasy_identity shopeasy_products shopeasy_orders shopeasy_inventory shopeasy_payments"
work=${RUNNER_TEMP:-$(mktemp -d)}
backup="$work/backup"

# "<database> <table> <rows>" for every table of every database (count(*) of each, not estimates)
count_rows() {
  for database in $databases; do
    docker compose exec -T postgres psql -U shopeasy -d "$database" -At -F ' ' -c "
      SELECT '$database', table_schema || '.' || table_name,
             (xpath('/row/c/text()', query_to_xml(
               format('select count(*) as c from %I.%I', table_schema, table_name), false, true, '')))[1]::text
        FROM information_schema.tables
       WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_type = 'BASE TABLE'
       ORDER BY 2"
  done
}

echo "::group::Stop the services and count the rows"
docker compose stop $apps
count_rows > "$work/rows-before.txt"
echo "$(wc -l < "$work/rows-before.txt") tables, $(awk '{ rows += $3 } END { print rows }' "$work/rows-before.txt") rows"
echo "::endgroup::"

echo "::group::Back up"
bash ops/backup.sh "$backup"
echo "::endgroup::"

echo "::group::Lose the data"
docker compose stop postgres
docker compose rm --force postgres
docker volume rm "$(docker volume ls --quiet --filter label=com.docker.compose.volume=postgres-data)"
docker compose up -d --wait postgres
if [ "$(count_rows | wc -l)" -ne 0 ]; then
  echo "::error::The new PostgreSQL is not empty, so the rehearsal proves nothing" >&2
  exit 1
fi
echo "PostgreSQL is back, empty"
echo "::endgroup::"

echo "::group::Restore"
started=$SECONDS
bash ops/restore.sh "$backup" --yes
echo "Restored in $((SECONDS - started)) seconds"
echo "::endgroup::"

echo "::group::Compare"
count_rows > "$work/rows-after.txt"
if ! diff "$work/rows-before.txt" "$work/rows-after.txt"; then
  echo "::error::The restored databases differ from what was backed up" >&2
  exit 1
fi
echo "All $(wc -l < "$work/rows-after.txt") tables hold the same rows"
echo "::endgroup::"

echo "::group::Start the services on the restored data"
docker compose up -d --wait --wait-timeout 300 $apps
echo "::endgroup::"
