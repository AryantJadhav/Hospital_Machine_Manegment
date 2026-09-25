#!/usr/bin/env bash
# Builds the Linux container image the way a hospital's server would, starts it
# with its database, and checks the things that have gone wrong there before.
#
# Every Linux-only bug this project has had was invisible to the tests, which run
# the app directly on the CI machine: a web build that landed in the wrong folder
# so the image had no UI, no curl so the health check always failed, no pg_dump so
# a Linux install had no backups at all. Each of those was found by building the
# image by hand. This does it on every push.
#
# It runs beside anything already on the machine: its own ports, volumes and
# network, all removed at the end.
#
#   bash tools/container-smoke-test.sh
set -uo pipefail

cd "$(dirname "$0")/.."

export HOSPITAL_NAME="Container Test Hospital"
export POSTGRES_PASSWORD="ci-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"
export APP_PORT="${APP_PORT:-5190}"
export DB_PORT="${DB_PORT:-5490}"
export PGDATA_VOLUME="hpm-smoke-pgdata"
export APPDATA_VOLUME="hpm-smoke-appdata"
export NETWORK_NAME="hpm-smoke"
# Off in a real install. This test takes a backup and a data export in the container, so it turns those on.
export FEATURE_BACKUPS=true
export FEATURE_EXPORT=true

PROJECT="hpmsmoke"
COMPOSE=(docker compose -p "$PROJECT")
URL="http://localhost:${APP_PORT}"
WORK="$(mktemp -d)"
FAILED=0

# JSON is read with Python, which every CI runner and most servers have, rather
# than jq, which a developer's machine may not.
PY=""
for candidate in python3 python; do
    # Tried, not just found: Windows ships a python3 stub that does not run.
    if "$candidate" -c "import sys" >/dev/null 2>&1; then PY="$candidate"; break; fi
done
[ -n "$PY" ] || { echo "Python is needed to read the responses."; exit 1; }
json() { "$PY" -c "import sys, json; d = json.load(sys.stdin); $1"; }
json_file() { "$PY" -c "import sys, json; d = json.load(open(sys.argv[1])); $2" "$1"; }

pass() { printf '    ok    %s\n' "$1"; }
fail() { printf '    FAIL  %s\n' "$1"; FAILED=$((FAILED + 1)); }

check() { # description, then a command; the command's success is the result
    local what="$1"; shift
    if "$@" >/dev/null 2>&1; then pass "$what"; else fail "$what"; fi
}

cleanup() {
    if [ "$FAILED" -ne 0 ]; then
        echo
        echo "==> Container logs"
        "${COMPOSE[@]}" logs --no-color --tail 120 app 2>&1 || true
    fi
    "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

wait_healthy() {
    for _ in $(seq 1 90); do
        if [ "$(curl -s -o /dev/null -w '%{http_code}' "$URL/health")" = "200" ]; then
            return 0
        fi
        sleep 2
    done
    return 1
}

signin() {
    curl -s -X POST "$URL/api/auth/login" -H 'content-type: application/json' \
        -d '{"userName":"admin","password":"Container-Test-2026"}' | json "print(d.get('accessToken', ''))"
}

echo "==> Building the image and starting it with its database"
"${COMPOSE[@]}" up -d --build || { fail "the image builds and starts"; exit 1; }
pass "the image builds and starts"

echo "==> Waiting for the app"
if wait_healthy; then pass "/health answers 200"; else fail "/health answers 200"; exit 1; fi

# The container's own health check, which calls curl inside the image.
for _ in $(seq 1 30); do
    state="$("${COMPOSE[@]}" ps --format '{{.Health}}' app 2>/dev/null | head -1)"
    [ "$state" = "healthy" ] && break
    sleep 2
done
check "the container reports itself healthy (curl is in the image)" test "$state" = "healthy"

echo "==> What the image serves"
serves_the_app() { curl -s "$URL/" | grep -i '<div id="root"' >/dev/null; }
check "the web app is served from /" serves_the_app
needs_setup() { curl -s "$URL/api/setup/status" | json "sys.exit(0 if d.get('needsSetup') is True else 1)"; }
check "a first-run install asks for its administrator" needs_setup

echo "==> Setting it up"
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$URL/api/setup/first-admin" \
    -H 'content-type: application/json' \
    -d '{"userName":"admin","fullName":"Container Admin","password":"Container-Test-2026"}')"
check "the administrator can be created" test "$code" = "200" -o "$code" = "201"

TOKEN="$(signin)"
check "the administrator can sign in" test -n "$TOKEN"
AUTH="Authorization: Bearer $TOKEN"

echo "==> Things that need what only Linux images lack"
# QuestPDF with its bundled font, on an image with no system fonts at all.
curl -s -o "$WORK/report.pdf" -H "$AUTH" \
    "$URL/api/reports/pm-compliance/report.pdf?from=2026-01-01&to=2026-01-31"
is_pdf() { [ "$(head -c 4 "$WORK/report.pdf")" = "%PDF" ]; }
check "a PDF is produced on an image with no system fonts" is_pdf

# pg_dump, which the image installs from the PostgreSQL project's repository.
curl -s -X POST -H "$AUTH" "$URL/api/admin/backups/run" -o "$WORK/backup.json"
backup_ok() { json_file "$WORK/backup.json" "sys.exit(0 if d.get('status') == 20 and d.get('sizeBytes', 0) > 0 else 1)"; }
# On the volume, not inside the container: a backup that dies with the container
# is not a backup. This once wrote to /app/backups.
backup_on_volume() { "${COMPOSE[@]}" exec -T app sh -c 'ls /var/lib/hospitalpm/backups | grep dump >/dev/null'; }
check "a backup succeeds (pg_dump is in the image)" backup_ok
check "the backup file is on the data volume" backup_on_volume

curl -s -o "$WORK/export.zip" -w '%{content_type}' -H "$AUTH" "$URL/api/admin/export" > "$WORK/export.type"
export_is_zip() { grep application/zip "$WORK/export.type" >/dev/null; }
export_is_complete() { "$PY" -c "import sys, zipfile; sys.exit(0 if 'manifest.json' in zipfile.ZipFile(sys.argv[1]).namelist() else 1)" "$WORK/export.zip"; }
check "the data export is a zip" export_is_zip
check "the export is complete (manifest.json is in it)" export_is_complete

echo "==> Surviving a restart"
"${COMPOSE[@]}" restart app >/dev/null 2>&1
if wait_healthy; then pass "the app comes back after a restart"; else fail "the app comes back after a restart"; fi
check "the same administrator can still sign in (the data volume held)" test -n "$(signin)"

echo
if [ "$FAILED" -eq 0 ]; then
    echo "Container smoke test passed."
    exit 0
fi

echo "Container smoke test FAILED: $FAILED check(s)."
exit 1
