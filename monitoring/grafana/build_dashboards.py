#!/usr/bin/env python3
"""
Builds the four Hospital PM Grafana dashboards as JSON files in ./dashboards, plus the kustomization that turns them
into labelled ConfigMaps for Grafana's sidecar to load. The JSON is committed; this script is how it was made and how
to change it (edit here, run it, commit both).

    python monitoring/grafana/build_dashboards.py

Every query uses low-cardinality labels only: the route pattern, the status code, the database name. No equipment
number, user or free text can appear in a label, because the application never puts one there.
"""
import json
import pathlib

HERE = pathlib.Path(__file__).parent
OUT = HERE / "dashboards"
DS = {"type": "prometheus", "uid": "prometheus"}

_next_id = [1]


def target(expr, legend="", ref="A"):
    return {"datasource": DS, "expr": expr, "legendFormat": legend, "refId": ref, "editorMode": "code", "range": True}


def panel(title, exprs, x, y, w=8, h=7, kind="timeseries", unit="short", desc="", thresholds=None, minv=None, maxv=None, stack=False):
    """exprs: list of (promql, legend)."""
    pid = _next_id[0]
    _next_id[0] += 1
    steps = thresholds or [{"color": "green", "value": None}]
    defaults = {"unit": unit, "thresholds": {"mode": "absolute", "steps": steps}, "color": {"mode": "palette-classic"}}
    if minv is not None:
        defaults["min"] = minv
    if maxv is not None:
        defaults["max"] = maxv
    p = {
        "id": pid, "type": kind, "title": title, "description": desc, "datasource": DS,
        "gridPos": {"x": x, "y": y, "w": w, "h": h},
        "targets": [target(e, l, chr(65 + i)) for i, (e, l) in enumerate(exprs)],
        "fieldConfig": {"defaults": defaults, "overrides": []},
    }
    if kind == "timeseries":
        p["options"] = {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi", "sort": "desc"}}
        p["fieldConfig"]["defaults"]["custom"] = {"lineWidth": 1, "fillOpacity": 15 if stack else 5,
                                                  "stacking": {"mode": "normal" if stack else "none"}}
    elif kind == "stat":
        p["options"] = {"reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
                        "colorMode": "background", "graphMode": "none", "textMode": "auto"}
    elif kind == "gauge":
        p["options"] = {"reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False}, "showThresholdMarkers": True}
    elif kind == "table":
        p["options"] = {"showHeader": True}
        p["transformations"] = [{"id": "reduce", "options": {"reducers": ["lastNotNull"]}}]
    return p


def dashboard(uid, title, panels, tags, description):
    return {
        "uid": uid, "title": title, "description": description, "tags": ["hospital-pm"] + tags,
        "timezone": "browser", "schemaVersion": 39, "version": 1, "editable": True, "graphTooltip": 1,
        "refresh": "30s", "time": {"from": "now-6h", "to": "now"},
        "templating": {"list": []}, "annotations": {"list": []}, "links": [], "panels": panels,
    }


GOOD_AT_ONE = [{"color": "red", "value": None}, {"color": "green", "value": 1}]
PCT = [{"color": "green", "value": None}, {"color": "orange", "value": 70}, {"color": "red", "value": 90}]
FS = 'fstype!~"tmpfs|overlay|squashfs|ramfs|fuse.*|nsfs|cgroup.*",mountpoint!~"/run.*|/var/lib/kubelet/pods/.*|/boot/efi"'
APP = 'job="hospitalpm-metrics"'
REQ = f"http_request_duration_seconds_count{{{APP}}}"

# ------------------------------------------------------------------------------------------------ A: infrastructure
infra = dashboard("hpm-infra", "Hospital PM: Infrastructure", [
    panel("Nodes ready", [('sum(kube_node_status_condition{condition="Ready",status="true"})', "ready")], 0, 0, 4, 4, "stat", thresholds=GOOD_AT_ONE),
    panel("CPU used", [('100 * (1 - avg(rate(node_cpu_seconds_total{mode="idle"}[5m])))', "cpu")], 4, 0, 4, 4, "gauge", "percent", thresholds=PCT, minv=0, maxv=100),
    panel("Memory used", [('100 * (1 - sum(node_memory_MemAvailable_bytes) / sum(node_memory_MemTotal_bytes))', "memory")], 8, 0, 4, 4, "gauge", "percent", thresholds=PCT, minv=0, maxv=100),
    panel("Workloads with replicas missing", [('sum(kube_deployment_status_replicas_unavailable) or vector(0)', "unavailable")], 12, 0, 4, 4, "stat",
          thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1}]),
    panel("Pod restarts, last hour", [('sum(increase(kube_pod_container_status_restarts_total[1h]))', "restarts")], 16, 0, 4, 4, "stat",
          thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 1}, {"color": "red", "value": 5}]),
    panel("Load (1 minute)", [('node_load1', "{{instance}}")], 20, 0, 4, 4, "stat"),
    panel("CPU used over time", [('100 * (1 - avg by (instance) (rate(node_cpu_seconds_total{mode="idle"}[5m])))', "{{instance}}")], 0, 4, 12, 8, unit="percent", minv=0, maxv=100),
    panel("Memory used over time", [('100 * (1 - node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes)', "{{instance}}")], 12, 4, 12, 8, unit="percent", minv=0, maxv=100),
    panel("Disk used, by filesystem", [(f'100 * (1 - node_filesystem_avail_bytes{{{FS}}} / node_filesystem_size_bytes{{{FS}}})', "{{mountpoint}}")], 0, 12, 12, 8,
          unit="percent", desc="Alerts at 85% (warning) and 95% (critical) used.", minv=0, maxv=100, thresholds=PCT),
    panel("Disk space left, by filesystem", [(f'node_filesystem_avail_bytes{{{FS}}}', "{{mountpoint}}")], 12, 12, 12, 8, unit="bytes"),
    panel("Network traffic", [('sum(rate(node_network_receive_bytes_total{device!~"lo|veth.*|cni.*|flannel.*|cali.*"}[5m]))', "received"),
                              ('sum(rate(node_network_transmit_bytes_total{device!~"lo|veth.*|cni.*|flannel.*|cali.*"}[5m]))', "sent")], 0, 20, 12, 8, unit="Bps"),
    panel("Disk reads and writes", [('sum(rate(node_disk_read_bytes_total[5m]))', "read"), ('sum(rate(node_disk_written_bytes_total[5m]))', "written")], 12, 20, 12, 8, unit="Bps"),
    panel("Container CPU, by namespace", [('sum by (namespace) (rate(container_cpu_usage_seconds_total{container!=""}[5m]))', "{{namespace}}")], 0, 28, 12, 8, unit="short", desc="Cores in use.", stack=True),
    panel("Container memory, by namespace", [('sum by (namespace) (container_memory_working_set_bytes{container!=""})', "{{namespace}}")], 12, 28, 12, 8, unit="bytes", stack=True),
    panel("Pods that restarted, last hour", [('sum by (namespace, pod) (increase(kube_pod_container_status_restarts_total[1h])) > 0', "{{namespace}}/{{pod}}")], 0, 36, 24, 7, "table"),
], ["infrastructure"], "The server: CPU, memory, disk, network, and what the containers on it use.")

# ------------------------------------------------------------------------------------------------ B: backend
backend = dashboard("hpm-backend", "Hospital PM: Backend", [
    panel("Backend up", [(f'min(up{{{APP}}})', "up")], 0, 0, 4, 4, "stat", thresholds=GOOD_AT_ONE, desc="1 when Prometheus can read the application's numbers."),
    panel("Requests per second", [(f'sum(rate({REQ}[5m]))', "all")], 4, 0, 5, 4, "stat", "reqps"),
    panel("Server errors (5xx)", [(f'100 * (sum(rate({REQ[:-1]},code=~"5.."}}[5m])) or vector(0)) / sum(rate({REQ}[5m]))', "5xx")], 9, 0, 5, 4, "stat", "percent",
          thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 1}, {"color": "red", "value": 5}], desc="Alerts above 5% with real traffic."),
    panel("p95 response time", [(f'histogram_quantile(0.95, sum by (le) (rate(http_request_duration_seconds_bucket{{{APP}}}[5m])))', "p95")], 14, 0, 5, 4, "stat", "s",
          thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 1}, {"color": "red", "value": 2}]),
    panel("Requests being handled now", [(f'sum(http_requests_in_progress{{{APP}}})', "in progress")], 19, 0, 5, 4, "stat"),
    panel("Requests per second, by outcome",
          [(f'(sum(rate({REQ[:-1]},code=~"2.."}}[5m])) or vector(0))', "2xx ok"), (f'(sum(rate({REQ[:-1]},code=~"3.."}}[5m])) or vector(0))', "3xx"),
           (f'(sum(rate({REQ[:-1]},code=~"4.."}}[5m])) or vector(0))', "4xx refused"), (f'(sum(rate({REQ[:-1]},code=~"5.."}}[5m])) or vector(0))', "5xx failed")], 0, 4, 12, 8, unit="reqps", stack=True),
    panel("Response time: p50, p95, p99",
          [(f'histogram_quantile({q}, sum by (le) (rate(http_request_duration_seconds_bucket{{{APP}}}[5m])))', f"p{int(q*100)}") for q in (0.5, 0.95, 0.99)], 12, 4, 12, 8, unit="s"),
    panel("Busiest routes", [(f'topk(10, sum by (route) (rate({REQ}[5m])))', "{{route}}")], 0, 12, 12, 8, unit="reqps",
          desc="By the route's pattern, so /api/equipment/{id:int} is one line however many machines there are."),
    panel("Slowest routes (p95)", [(f'topk(10, histogram_quantile(0.95, sum by (le, route) (rate(http_request_duration_seconds_bucket{{{APP}}}[5m]))))', "{{route}}")], 12, 12, 12, 8, unit="s",
          desc="PDFs and the full export are slow by nature."),
    panel("Failing routes (5xx per second)", [(f'topk(10, sum by (route) (rate({REQ[:-1]},code=~"5.."}}[5m])))', "{{route}}")], 0, 20, 12, 8, unit="reqps"),
    panel("Refused requests (4xx) by status", [(f'sum by (code) (rate({REQ[:-1]},code=~"4.."}}[5m]))', "{{code}}")], 12, 20, 12, 8, unit="reqps"),
    panel("Database could not be reached", [('increase(hospitalpm_database_connection_failures_total[5m])', "connection failures"),
                                            ('increase(hospitalpm_database_timeouts_total[5m])', "timeouts")], 0, 28, 12, 8, desc="Requests per 5 minutes that failed because of the database."),
    panel("Saves the database refused", [('sum by (reason) (increase(hospitalpm_database_refusals_total[15m]))', "{{reason}}")], 12, 28, 12, 8,
          desc="Duplicates and values too long: the caller's doing, not a fault."),
    panel("Process memory", [(f'process_working_set_bytes{{{APP}}}', "working set"), (f'dotnet_total_memory_bytes{{{APP}}}', ".NET heap")], 0, 36, 12, 8, unit="bytes"),
    panel("Process CPU (cores)", [(f'rate(process_cpu_seconds_total{{{APP}}}[5m])', "cpu")], 12, 36, 12, 8, unit="short"),
], ["application"], "Hospital PM's own numbers: traffic, speed, errors, and database trouble. Low-cardinality labels only.")

# ------------------------------------------------------------------------------------------------ C: postgres
DB = 'datname="hospitalpm"'
postgres = dashboard("hpm-postgres", "Hospital PM: PostgreSQL", [
    panel("PostgreSQL up", [('pg_up', "up")], 0, 0, 4, 4, "stat", thresholds=GOOD_AT_ONE),
    panel("Connections in use", [('sum(pg_stat_activity_count)', "connections")], 4, 0, 4, 4, "stat"),
    panel("Connections used of the limit", [('100 * sum(pg_stat_activity_count) / max(pg_settings_max_connections)', "used")], 8, 0, 4, 4, "gauge", "percent", thresholds=PCT, minv=0, maxv=100,
          desc="Uses the exporter's settings metrics (pg_settings_max_connections)."),
    panel("Cache hit ratio", [(f'100 * sum(rate(pg_stat_database_blks_hit{{{DB}}}[5m])) / (sum(rate(pg_stat_database_blks_hit{{{DB}}}[5m])) + sum(rate(pg_stat_database_blks_read{{{DB}}}[5m])))', "hit")],
          12, 0, 4, 4, "gauge", "percent", thresholds=[{"color": "red", "value": None}, {"color": "orange", "value": 90}, {"color": "green", "value": 98}], minv=0, maxv=100),
    panel("Database size", [(f'pg_database_size_bytes{{{DB}}}', "size")], 16, 0, 4, 4, "stat", "bytes"),
    panel("Deadlocks, last hour", [(f'sum(increase(pg_stat_database_deadlocks{{{DB}}}[1h]))', "deadlocks")], 20, 0, 4, 4, "stat",
          thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1}]),
    panel("Connections by state", [('sum by (state) (pg_stat_activity_count)', "{{state}}")], 0, 4, 12, 8, stack=True, desc="active, idle, idle in transaction."),
    panel("Backends per database", [('pg_stat_database_numbackends', "{{datname}}")], 12, 4, 12, 8),
    panel("Transactions per second", [(f'sum(rate(pg_stat_database_xact_commit{{{DB}}}[5m]))', "commits"), (f'sum(rate(pg_stat_database_xact_rollback{{{DB}}}[5m]))', "rollbacks")], 0, 12, 12, 8, unit="ops"),
    panel("Rows per second", [(f'sum(rate(pg_stat_database_tup_returned{{{DB}}}[5m]))', "read (scanned)"), (f'sum(rate(pg_stat_database_tup_fetched{{{DB}}}[5m]))', "fetched"),
                              (f'sum(rate(pg_stat_database_tup_inserted{{{DB}}}[5m]))', "inserted"), (f'sum(rate(pg_stat_database_tup_updated{{{DB}}}[5m]))', "updated"),
                              (f'sum(rate(pg_stat_database_tup_deleted{{{DB}}}[5m]))', "deleted")], 12, 12, 12, 8, unit="ops"),
    panel("Locks held, by mode", [(f'sum by (mode) (pg_locks_count{{{DB}}})', "{{mode}}")], 0, 20, 12, 8),
    panel("Database size over time", [(f'pg_database_size_bytes{{{DB}}}', "hospitalpm")], 12, 20, 12, 8, unit="bytes"),
    panel("Disk reads vs memory hits", [(f'sum(rate(pg_stat_database_blks_hit{{{DB}}}[5m]))', "from memory"), (f'sum(rate(pg_stat_database_blks_read{{{DB}}}[5m]))', "from disk")], 0, 28, 12, 8, unit="ops"),
    panel("Conflicts and temp files", [(f'sum(rate(pg_stat_database_conflicts{{{DB}}}[5m]))', "conflicts"), (f'sum(rate(pg_stat_database_temp_files{{{DB}}}[5m]))', "temp files")], 12, 28, 12, 8, unit="ops",
          desc="Replication is not used on this install, so there is no replication panel."),
], ["database"], "PostgreSQL: availability, connections, activity, cache, locks and size. No query text and no table contents.")

# ------------------------------------------------------------------------------------------------ D: kubernetes
kube = dashboard("hpm-kubernetes", "Hospital PM: Kubernetes", [
    panel("Nodes ready", [('sum(kube_node_status_condition{condition="Ready",status="true"})', "ready"), ('sum(kube_node_status_condition{condition="Ready",status!="true"})', "not ready")], 0, 0, 5, 4, "stat",
          thresholds=GOOD_AT_ONE),
    panel("Pods pending or failed", [('sum(kube_pod_status_phase{phase=~"Pending|Failed"})', "pods")], 5, 0, 5, 4, "stat", thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1}]),
    panel("Pods running", [('sum(kube_pod_status_phase{phase="Running"})', "running")], 10, 0, 5, 4, "stat"),
    panel("Memory pressure / disk pressure / PIDs", [('sum(kube_node_status_condition{condition=~"MemoryPressure|DiskPressure|PIDPressure",status="true"})', "pressure")], 15, 0, 5, 4, "stat",
          thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1}]),
    panel("Restarts, last hour", [('sum(increase(kube_pod_container_status_restarts_total[1h]))', "restarts")], 20, 0, 4, 4, "stat",
          thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 1}, {"color": "red", "value": 5}]),
    panel("Deployments: wanted and available", [('sum by (namespace, deployment) (kube_deployment_spec_replicas)', "wanted {{namespace}}/{{deployment}}"),
                                                 ('sum by (namespace, deployment) (kube_deployment_status_replicas_available)', "available {{namespace}}/{{deployment}}")], 0, 4, 12, 8),
    panel("StatefulSets: wanted and ready", [('sum by (namespace, statefulset) (kube_statefulset_replicas)', "wanted {{namespace}}/{{statefulset}}"),
                                             ('sum by (namespace, statefulset) (kube_statefulset_status_replicas_ready)', "ready {{namespace}}/{{statefulset}}")], 12, 4, 12, 8),
    panel("Pod restarts over time", [('sum by (namespace, pod) (increase(kube_pod_container_status_restarts_total[15m]))', "{{namespace}}/{{pod}}")], 0, 12, 12, 8),
    panel("Pods by phase", [('sum by (phase) (kube_pod_status_phase)', "{{phase}}")], 12, 12, 12, 8, stack=True),
    panel("CPU: requested vs used (cores)", [('sum(kube_pod_container_resource_requests{resource="cpu"})', "requested"),
                                             ('sum(rate(container_cpu_usage_seconds_total{container!=""}[5m]))', "used")], 0, 20, 12, 8),
    panel("Memory: requested vs used", [('sum(kube_pod_container_resource_requests{resource="memory"})', "requested"),
                                        ('sum(container_memory_working_set_bytes{container!=""})', "used")], 12, 20, 12, 8, unit="bytes"),
    panel("Memory used vs limit, Hospital PM", [('max by (pod) (container_memory_working_set_bytes{namespace="hospitalpm",container!=""})', "used {{pod}}"),
                                                ('max by (pod) (kube_pod_container_resource_limits{namespace="hospitalpm",resource="memory"})', "limit {{pod}}")], 0, 28, 12, 8, unit="bytes"),
    panel("CPU used, Hospital PM (cores)", [('sum by (pod) (rate(container_cpu_usage_seconds_total{namespace="hospitalpm",container!=""}[5m]))', "{{pod}}")], 12, 28, 12, 8),
], ["kubernetes"], "The cluster: nodes, workloads, restarts, and what is asked for against what is used.")

for name, d in (("infrastructure", infra), ("backend", backend), ("postgresql", postgres), ("kubernetes", kube)):
    (OUT / f"{name}.json").write_text(json.dumps(d, indent=2) + "\n", encoding="utf-8")
    print("wrote", name, len(d["panels"]), "panels")

kustomization = """# Turns the dashboards into ConfigMaps that Grafana's sidecar loads (label grafana_dashboard=1).
#   kubectl apply -k monitoring/grafana
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: monitoring
generatorOptions:
  disableNameSuffixHash: true
  labels:
    grafana_dashboard: "1"
  annotations:
    grafana_folder: "Hospital PM"
configMapGenerator:
"""
for name in ("infrastructure", "backend", "postgresql", "kubernetes"):
    kustomization += f"  - name: hpm-dashboard-{name}\n    files:\n      - dashboards/{name}.json\n"
(HERE / "kustomization.yaml").write_text(kustomization, encoding="utf-8")
print("wrote kustomization.yaml")
