#!/usr/bin/env bash
# Asks Prometheus whether the monitoring is doing its job: are the targets up, are Hospital PM's numbers arriving, is the
# database exporter connected, and what is firing. Read-only. Run ON THE SERVER (where kubectl works):
#
#     bash monitoring/check-stack.sh
set -uo pipefail
export KUBECONFIG="${KUBECONFIG:-$HOME/.kube/config}"
PORT="${PORT:-19090}"

kubectl -n monitoring port-forward svc/kps-prometheus "$PORT:9090" >/dev/null 2>&1 &
PF=$!
trap 'kill $PF 2>/dev/null' EXIT
for _ in $(seq 1 20); do curl -s "localhost:$PORT/-/ready" >/dev/null 2>&1 && break; sleep 1; done

q() {
  curl -s --data-urlencode "query=$1" "localhost:$PORT/api/v1/query" | python3 -c '
import json, sys
r = json.load(sys.stdin)["data"]["result"]
print("  ", [(x["metric"].get("route") or x["metric"].get("job") or x["metric"].get("alertname") or "", x["value"][1]) for x in r][:8])'
}

echo "targets:"
curl -s "localhost:$PORT/api/v1/targets" | python3 -c '
import json, sys
d = json.load(sys.stdin)["data"]["activeTargets"]
bad = [(t["labels"]["job"], t["lastError"][:80]) for t in d if t["health"] != "up"]
print("   total", len(d), "| not up:", bad)'
echo "Hospital PM reachable (1 = yes):";            q 'up{job="hospitalpm-metrics"}'
echo "requests by route, last 10 minutes:";         q 'topk(8, sum by (route)(increase(http_request_duration_seconds_count{job="hospitalpm-metrics"}[10m])))'
echo "server errors (5xx), last 10 minutes:";       q 'sum(increase(http_request_duration_seconds_count{job="hospitalpm-metrics",code=~"5.."}[10m])) or vector(0)'
echo "database connection failures (counter):";     q 'hospitalpm_database_connection_failures_total'
echo "PostgreSQL up (1 = yes):";                    q 'pg_up'
echo "PostgreSQL connections used, percent:";       q '100*sum(pg_stat_activity_count)/max(pg_settings_max_connections)'
echo "Alerts not quiet:"
curl -s "localhost:$PORT/api/v1/alerts" | python3 -c '
import json, sys
a = json.load(sys.stdin)["data"]["alerts"]
print("  ", [(x["labels"]["alertname"], x["state"], x["labels"].get("mountpoint", "")) for x in a if x["state"] != "inactive"])'
