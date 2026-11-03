#!/bin/sh
# Creates (or, when it exists, updates) the role the PostgreSQL exporter signs in with. Mounted into the
# database container by docker-compose.yml and run by its entrypoint when the data directory is created.
# README, "Dependencies".
#
# The role may only LOOK: pg_monitor is a built-in read-only role for monitoring tools (it can read the
# statistics of every connection and database, and nothing else). It cannot read a table of any service.
# The password comes from POSTGRES_EXPORTER_PASSWORD in .env, never from the repository.
#
# The entrypoint runs this only for a NEW database volume. For one that already exists, or to change the
# password after editing .env, run it by hand (it is safe to run again):
#   docker compose up -d postgres
#   docker compose exec postgres sh /docker-entrypoint-initdb.d/20-monitoring-role.sh
: "${POSTGRES_EXPORTER_PASSWORD:?POSTGRES_EXPORTER_PASSWORD is not set}"

psql -v ON_ERROR_STOP=1 -v exporter_password="$POSTGRES_EXPORTER_PASSWORD" \
     --username "${POSTGRES_USER:-postgres}" --dbname postgres <<'SQL'
SELECT format('%s ROLE postgres_exporter LOGIN PASSWORD %L',
              CASE WHEN EXISTS (SELECT FROM pg_roles WHERE rolname = 'postgres_exporter') THEN 'ALTER' ELSE 'CREATE' END,
              :'exporter_password') \gexec
GRANT pg_monitor TO postgres_exporter;
SQL
