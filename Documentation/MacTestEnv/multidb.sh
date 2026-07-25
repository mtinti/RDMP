#!/usr/bin/env bash
# Start/stop the optional PostgreSQL + Oracle test containers and wire them into the RDMP test config.
#
#   bash Documentation/MacTestEnv/multidb.sh up        # start postgres + oracle, create scratch DB, enable test lines
#   bash Documentation/MacTestEnv/multidb.sh down      # stop them (volumes kept) and disable the test lines
#   bash Documentation/MacTestEnv/multidb.sh status    # show container + config state
#
# See README.md section "Multi-DBMS testing (PostgreSQL + Oracle)".
set -euo pipefail
cd "$(dirname "$0")/../.."

COMPOSE="docker compose -f Documentation/MacTestEnv/docker-compose.yml"
TD=Tests.Common/TestDatabases.txt
PG_LINE='PostgreSql:\tUser ID=postgres;Password=YourStrong!Passw0rd;Host=127.0.0.1;Port=5432'
ORA_LINE='Oracle:\tData Source=localhost:1521/FREEPDB1;User Id=system;Password=YourStrong1Passw0rd;'

wait_healthy() { # containerName timeoutSeconds
  local c=$1 t=$2 i=0
  until [ "$(docker inspect -f '{{.State.Health.Status}}' "$c" 2>/dev/null)" = "healthy" ]; do
    i=$((i+5)); [ $i -ge "$t" ] && { echo "TIMEOUT waiting for $c"; exit 1; }
    sleep 5
  done
  echo "$c healthy"
}

case "${1:-}" in
  up)
    $COMPOSE --profile postgres --profile oracle up -d
    wait_healthy rdmp-postgres 120
    wait_healthy rdmp-oracle 600   # first boot creates the database (minutes)

    # RDMP tests expect the scratch database to already exist on PostgreSQL (case-sensitive name)
    docker exec rdmp-postgres psql -U postgres -tc \
      "SELECT 1 FROM pg_database WHERE datname='TEST_ScratchArea'" | grep -q 1 || \
      docker exec rdmp-postgres psql -U postgres -c 'CREATE DATABASE "TEST_ScratchArea";'
    echo "postgres scratch database ready"

    # enable the connection lines in the ACTIVE test config (idempotent)
    grep -q '^PostgreSql:' "$TD" || sed -i '' "s|^#PostgreSql:.*|$(printf "$PG_LINE")|" "$TD"
    grep -q '^Oracle:.*Data Source' "$TD" || sed -i '' "s|^#\{0,1\}Oracle:.*|$(printf "$ORA_LINE")|" "$TD"
    echo "enabled in $TD:"
    grep -E '^(PostgreSql|Oracle):' "$TD"
    echo
    echo "NOTE: rebuild the test project once so the config copies to the output dir:"
    echo "  dotnet build Rdmp.Core.Tests/Rdmp.Core.Tests.csproj -c Debug -p:WarningsNotAsErrors='\"NU1902;NU1903;NU1904\"'"
    ;;
  down)
    $COMPOSE --profile postgres --profile oracle stop postgres oracle
    # disable the lines so absent containers cause Skip, not failures
    sed -i '' 's|^PostgreSql:|#PostgreSql:|' "$TD" || true
    sed -i '' 's|^Oracle:.*Data Source.*|Oracle:|' "$TD" || true
    echo "containers stopped (volumes kept); test lines disabled"
    ;;
  status)
    docker ps --format '{{.Names}}\t{{.Status}}' | grep -E 'rdmp-(postgres|oracle)' || echo "not running"
    grep -E '^#?(PostgreSql|Oracle):' "$TD"
    ;;
  *)
    echo "usage: $0 {up|down|status}"; exit 1 ;;
esac
