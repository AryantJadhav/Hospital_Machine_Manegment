#!/usr/bin/env bash
# An isolated copy of Hospital PM to break: a throwaway database and the app, on a network of their own, with
# the CPU and memory limits of a modest hospital PC so the breaking point is reachable.
#
#   bash tools/stresstest/local-stack.sh up       start, and fill the demo hospital
#   bash tools/stresstest/local-stack.sh down     remove everything it made (containers, network, volumes)
#
# Nothing here touches an installed copy, a compose project, or any other container: every name starts "stress-".
#
# Settings (environment): APP_CPUS=2  APP_MEM=1g  DB_CPUS=2  DB_MEM=1g  PORT=5600  BIND=127.0.0.1  SKIP_DEMO=0
#                         IMAGE=aryant050/hospitalpm:v0.0.7
#
# The port is published on 127.0.0.1 only, so nothing else on the network can reach it. Container logs are capped (the
# app logs every SQL command, which under load would otherwise be gigabytes). SKIP_DEMO=1 leaves the register empty:
# fill it yourself, for example through an SSH tunnel.
set -euo pipefail

APP_CPUS="${APP_CPUS:-2}"; APP_MEM="${APP_MEM:-1g}"; DB_CPUS="${DB_CPUS:-2}"; DB_MEM="${DB_MEM:-1g}"
PORT="${PORT:-5600}"; BIND="${BIND:-127.0.0.1}"; SKIP_DEMO="${SKIP_DEMO:-0}"; IMAGE="${IMAGE:-aryant050/hospitalpm:v0.0.7}"; DBIMAGE="${DBIMAGE:-aryant050/hospitalpm:DB}"
PASSWORD="Stress-Test-1"; ADMIN="admin"; ADMINPW="Stress-Admin-1"
export MSYS_NO_PATHCONV=1

case "${1:-up}" in
  down)
    docker rm -f stress-app stress-db >/dev/null 2>&1 || true
    docker network rm stress-net >/dev/null 2>&1 || true
    docker volume rm stress-pgdata stress-appdata >/dev/null 2>&1 || true
    echo "removed"
    ;;
  up)
    docker network create stress-net >/dev/null
    docker volume create stress-pgdata >/dev/null; docker volume create stress-appdata >/dev/null
    docker run -d --name stress-db --network stress-net --cpus "$DB_CPUS" --memory "$DB_MEM" \
      --log-opt max-size=10m --log-opt max-file=2 \
      -e POSTGRES_PASSWORD="$PASSWORD" -v stress-pgdata:/var/lib/postgresql "$DBIMAGE" >/dev/null
    for i in $(seq 1 40); do [ "$(docker inspect stress-db --format '{{.State.Health.Status}}')" = healthy ] && break; sleep 2; done
    docker run -d --name stress-app --network stress-net -p "$BIND:$PORT:5000" --cpus "$APP_CPUS" --memory "$APP_MEM" \
      --log-opt max-size=10m --log-opt max-file=2 \
      -e ConnectionStrings__HospitalPm="Host=stress-db;Port=5432;Database=hospitalpm;Username=hospitalpm;Password=$PASSWORD" \
      -e Reports__HospitalName="Stress Test" -e FirstRun__AdminUserName="$ADMIN" -e FirstRun__AdminPassword="$ADMINPW" \
      -e Features__Backups=true -e Features__Export=true -e Mdns__Enabled=false \
      -v stress-appdata:/var/lib/hospitalpm "$IMAGE" >/dev/null
    for i in $(seq 1 60); do curl -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:$PORT/health" | grep -q 200 && break; sleep 2; done
    curl -s "http://127.0.0.1:$PORT/health"; echo
    if [ "$SKIP_DEMO" != "1" ]; then
      echo "filling the demo hospital..."
      dotnet run --project "$(dirname "$0")/../HospitalPm.DemoData" -- --url "http://127.0.0.1:$PORT" --user "$ADMIN" --password "$ADMINPW" | tail -4
    fi
    cat <<MSG

ready:   http://127.0.0.1:$PORT      (app limited to $APP_CPUS CPUs and $APP_MEM, database $DB_CPUS CPUs and $DB_MEM)
run:     node tools/stresstest/stress.mjs --url http://127.0.0.1:$PORT --user $ADMIN --password '$ADMINPW' --containers stress-app,stress-db
remove:  bash tools/stresstest/local-stack.sh down
MSG
    ;;
  *) echo "use: up | down"; exit 1;;
esac
