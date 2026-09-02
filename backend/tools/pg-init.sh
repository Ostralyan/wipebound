#!/bin/bash
# Apply every migration, in order, to both databases when the cluster is first
# created.
#
# TWO databases, because the integration tests write real rows through the real
# router and were doing it into the development database. Every `cargo test` run
# left behind runs with names like boss-1ca1046c and no combat log, until the
# site was mostly test fixtures and a genuine fight was hard to find.
#
# Iterating the directory means adding a migration needs no change here.
set -euo pipefail

TEST_DB="${POSTGRES_DB}_test"
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "CREATE DATABASE ${TEST_DB};"

for target in "$POSTGRES_DB" "$TEST_DB"; do
  for dir in $(find /migrations -mindepth 1 -maxdepth 1 -type d | sort); do
    echo "[migrate] $target <- ${dir##*/}"
    psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$target" -f "$dir/up.sql"
  done
done
