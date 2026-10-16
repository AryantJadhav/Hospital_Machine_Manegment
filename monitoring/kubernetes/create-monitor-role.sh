#!/usr/bin/env bash
# Makes the read-only PostgreSQL login the exporter uses, with a password made here and kept only in a Kubernetes
# Secret. Nothing is written to a file. Run it on the machine where kubectl reaches the cluster, after Hospital PM
# is up and before installing the exporter. Safe to run again: it sets a new password and replaces the Secret.
#
#   bash monitoring/kubernetes/create-monitor-role.sh
set -euo pipefail

NS_APP="${NS_APP:-hospitalpm}"
NS_MON="${NS_MON:-monitoring}"
POD="${POD:-hospitalpm-db-0}"
ROLE="hospitalpm_monitor"

PASSWORD="$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 28)"

# pg_monitor is PostgreSQL's built-in role for exactly this: read the statistics views, nothing else. No access to any
# table of the product, no write of any kind. CONNECT is needed to log in to the one database.
kubectl -n "$NS_APP" exec -i "$POD" -- psql -U hospitalpm -d hospitalpm -v ON_ERROR_STOP=1 -v pw="$PASSWORD" <<SQL
SELECT format('CREATE ROLE ${ROLE} LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE CONNECTION LIMIT 5', :'pw')
  WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${ROLE}') \gexec
SELECT format('ALTER ROLE ${ROLE} PASSWORD %L', :'pw') \gexec
GRANT pg_monitor TO ${ROLE};
GRANT CONNECT ON DATABASE hospitalpm TO ${ROLE};
SQL

kubectl get namespace "$NS_MON" >/dev/null
# sslmode=disable: the database image has no certificate, and the traffic never leaves the cluster's own network
# (the network policy allows only the exporter to reach it).
kubectl -n "$NS_MON" create secret generic pg-exporter-dsn \
  --from-literal=dsn="postgresql://${ROLE}:${PASSWORD}@hospitalpm-db.${NS_APP}.svc:5432/hospitalpm?sslmode=disable" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "Role ${ROLE} (read-only, pg_monitor) is ready and its login is in the Secret ${NS_MON}/pg-exporter-dsn."
