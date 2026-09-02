#!/bin/bash
# Apply every migration, in order, when the database is first created.
#
# Mounting one migration's up.sql was fine while there was one. It stops being
# fine the moment a second exists, and it fails silently: a fresh database gets
# the initial schema, the service starts, and only a query against the missing
# columns says anything is wrong.
#
# Iterating the directory means adding a migration needs no change here.
set -euo pipefail

for dir in $(find /migrations -mindepth 1 -maxdepth 1 -type d | sort); do
  echo "[migrate] ${dir##*/}"
  psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f "$dir/up.sql"
done
