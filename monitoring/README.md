# Monitoring Hospital PM: Prometheus, Grafana and Alertmanager on Kubernetes

For whoever runs a Hospital PM **test or pilot server**. It is **not part of a hospital's install**: the product still
installs as one program and PostgreSQL, with no Kubernetes (see `CLAUDE.md`). This folder is the vendor-side way to watch
and load-test a copy of it. The product itself holds no patient data, and nothing here adds any.

Everything below that says "ON THE SERVER" runs on the Linux server; "ON YOUR PC" runs on your own computer.

## 1. What was found (12 October 2026)

| | |
|---|---|
| Server | Ubuntu 26.04, 32 CPUs, 61 GB RAM, shared with about 15 other people. Root disk `/` 295 GB, **99% full** (other people's files); `/data` 1.8 TB, 25% used. `ufw` active. Docker 29. |
| Before | **No Kubernetes.** Hospital PM ran under Docker Compose (`~/hospitalpm`, port 5000). No Helm. |
| Application | .NET 10, port 5000, `/health` (does not touch the database), **no metrics**. PostgreSQL 18.6 (`aryant050/hospitalpm:DB`). |
| Images | `aryant050/hospitalpm:v0.0.10` (the application, with metrics), `aryant050/hospitalpm:DB`. |

Two facts from the server shaped the install, and are why `monitoring/k3s/` exists:

1. **The root disk is nearly full**, so k3s's node agent reported *disk pressure* and refused to start any pod. Its working
   folder is now on `/data` (`fix-kubelet-root.sh`; `install-k3s.sh` does it from the start).
2. k3s opens ports **9100, 6443 and 10250** to every network interface. They answered from another computer on the
   Tailscale network. `restrict-ports.sh` closes them to everything but the server and the cluster's pods.

## 2. Architecture

```
 your PC ──ssh tunnel──▶ Grafana (3000)      nothing is published to the network
                           │
 namespace monitoring      ▼
 ┌─────────────────────────────────────────────────────────────────────────────────────────────┐
 │ Prometheus ◀── scrapes every 30 s ──┬─ Hospital PM :9102 /metrics   (separate port)         │
 │   30 days, 25 GB cap, 30 Gi volume  ├─ PostgreSQL exporter :9187 ──(read-only login)──┐     │
 │ Alertmanager ─▶ alert-sink (test)   ├─ node exporter :9100 (host)                     │     │
 │ kube-state-metrics, operator        └─ kubelet / cAdvisor (containers)                │     │
 └───────────────────────────────────────────────────────────────────────────────────────┼─────┘
 namespace hospitalpm: Hospital PM (1 pod) ───────── PostgreSQL (1 pod) ◀────────────────┘
 namespace loadtest:   k6 (a Job, only while a test runs) ──▶ Hospital PM, and its figures ──▶ Prometheus
```

| Part | What it is |
|---|---|
| k3s v1.36.5+k3s1 | One-node Kubernetes. Data in `/data/k3s`. Traefik and servicelb switched off (the existing Caddy holds ports 80 and 443). |
| kube-prometheus-stack **92.3.0** | Prometheus, Grafana 13, Alertmanager, node exporter, kube-state-metrics, the operator. Release `kps`. |
| prometheus-postgres-exporter **8.2.0** | Release `pgexporter`, as the read-only role `hospitalpm_monitor` (`pg_monitor`; cannot read any table). |
| Hospital PM `v0.0.10` | `Metrics:Enabled` (off by default). Numbers on port **9102**, apart from the port people open (5000). |
| grafana/k6 1.3.0 | The load test. |

### What the application reports

Request counts and durations by **route pattern** (`/api/equipment/{id:int}`, never the address as typed), status code,
and method; requests in progress; process memory and CPU; and three counters of its own: database connection failures,
database timeouts, and saves the database refused (`duplicate`, `too_long`, `number_too_large`, `bad_character`). No
equipment number, user name, search text or request body is ever a label, and the product has no patient data to leak.

## 3. Files

```
monitoring/
  README.md                          this file
  check-stack.sh                     asks Prometheus: targets up? app numbers arriving? alerts firing?  (server)
  check-dashboards.py                runs every dashboard query through Grafana, reports empty panels   (server)
  helm/kube-prometheus-stack-values.yaml     versions pinned in the install command, sizes and retention here
  helm/postgres-exporter-values.yaml
  kubernetes/00-namespace.yaml               namespace "monitoring"
  kubernetes/app-servicemonitor.yaml         the metrics Service (in hospitalpm) and the ServiceMonitor
  kubernetes/network-policies.yaml           who may talk to whom (k3s enforces them itself)
  kubernetes/create-monitor-role.sh          makes the read-only database login; password only in a Secret
  kubernetes/alert-sink.yaml                 a TEST receiver that logs alerts (tells nobody)
  prometheus/rules.yaml                      18 alert rules about Hospital PM, PostgreSQL and the server
  grafana/build_dashboards.py                makes the four dashboards (edit it, run it, commit both)
  grafana/dashboards/*.json                  Infrastructure, Backend, PostgreSQL, Kubernetes (version-controlled)
  grafana/kustomization.yaml                 turns them into ConfigMaps Grafana loads
  k3s/install-k3s.sh  fix-kubelet-root.sh  restrict-ports.sh      run with sudo ON THE SERVER
  loadtest/k6-hospitalpm.js  loadtest/job.yaml                    the load test
deploy/kubernetes/20-app.yaml        the application, now with the optional METRICS_ENABLED switch
src/HospitalPm.Api/Hosting/ServerMetrics.cs    the metrics code (prometheus-net.AspNetCore 8.2.1)
```

## 4. Install, from nothing

Copy this repository's `monitoring/` and `deploy/kubernetes/` to the server (`~/monitoring-work/`). Helm is a single file
in `~/bin` (no sudo): `curl -fsSL https://get.helm.sh/helm-v3.19.0-linux-amd64.tar.gz | tar xz`, move `linux-amd64/helm`.

**ON THE SERVER, with sudo, once** (ask whoever administers it):

```bash
sudo bash monitoring/k3s/install-k3s.sh          # k3s, on /data, with the firewall rules it needs
sudo bash monitoring/k3s/restrict-ports.sh       # closes 9100, 6443, 10250 to the network
```

**ON THE SERVER, as your login** (every command uses `export KUBECONFIG=$HOME/.kube/config PATH=$HOME/bin:$PATH`):

```bash
# 1. namespace, and Grafana's login (a Secret; the password is never in Git)
kubectl apply -f monitoring/kubernetes/00-namespace.yaml
kubectl -n monitoring create secret generic grafana-admin --from-literal=admin-user=admin --from-literal=admin-password="$(head -c 24 /dev/urandom | base64 | tr -dc A-Za-z0-9 | head -c 24)"

# 2. the stack (pinned version)
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts && helm repo update
helm upgrade --install kps prometheus-community/kube-prometheus-stack --version 92.3.0 -n monitoring \
  -f monitoring/helm/kube-prometheus-stack-values.yaml --wait --timeout 15m

# 3. Hospital PM itself (skip if it is already running), with its metrics switched on
kubectl apply -f deploy/kubernetes/00-namespace.yaml
kubectl -n hospitalpm create secret generic hospitalpm-db --from-literal=password='CHOOSE-A-PASSWORD'
kubectl apply -f deploy/kubernetes/10-postgres.yaml -f deploy/kubernetes/20-app.yaml
kubectl -n hospitalpm patch configmap hospitalpm-config --type merge -p '{"data":{"METRICS_ENABLED":"true"}}'
kubectl -n hospitalpm rollout restart deployment/hospitalpm

# 4. the database exporter: makes the read-only login, keeps its password in a Secret
bash monitoring/kubernetes/create-monitor-role.sh
helm upgrade --install pgexporter prometheus-community/prometheus-postgres-exporter --version 8.2.0 -n monitoring \
  -f monitoring/helm/postgres-exporter-values.yaml --wait

# 5. rules, the application's scrape target, dashboards, the test receiver
kubectl apply -f monitoring/kubernetes/alert-sink.yaml -f monitoring/prometheus/rules.yaml -f monitoring/kubernetes/app-servicemonitor.yaml
kubectl apply -k monitoring/grafana

# 6. LAST, once step "Validate" shows every target up: the network policies
kubectl apply -f monitoring/kubernetes/network-policies.yaml
```

## 5. Validate, inspect, upgrade, roll back, remove

```bash
bash monitoring/check-stack.sh                                  # targets, app figures, PostgreSQL, alerts
kubectl -n monitoring get pods ; kubectl -n hospitalpm get pods
helm -n monitoring list ; helm -n monitoring history kps
helm template kps prometheus-community/kube-prometheus-stack --version 92.3.0 -n monitoring -f monitoring/helm/kube-prometheus-stack-values.yaml >/dev/null && echo renders
```

- **Upgrade the stack:** change the version and the values, run the same `helm upgrade --install`. Read the chart's
  release notes first; CRDs are not upgraded by Helm (apply them by hand when a major version says so).
- **Roll back:** `helm -n monitoring rollback kps <revision>` (revisions from `helm history`). Dashboards and rules are
  files: `git checkout` the old ones and `kubectl apply` again. Network policies: `kubectl delete -f monitoring/kubernetes/network-policies.yaml` puts everything open again at once.
- **Upgrade Hospital PM:** change the image tag in `deploy/kubernetes/20-app.yaml`, `kubectl apply`. It has one replica and
  the `Recreate` strategy: it is down for the seconds the new pod takes. Do not do it during a test.
- **Remove monitoring only:** `helm -n monitoring uninstall pgexporter kps`, then `kubectl delete namespace monitoring`
  (this deletes the stored history and Grafana's data). Hospital PM and its data are not touched.
- **Remove everything k3s:** `sudo /usr/local/bin/k3s-uninstall.sh` (this deletes the Kubernetes copy's data, not Docker's).

## 6. Using it

**Grafana** is never published. From your PC, in a terminal you leave open:

```bash
ssh -L 3300:127.0.0.1:13000 YOUR-LOGIN@YOUR-SERVER 'KUBECONFIG=$HOME/.kube/config kubectl -n monitoring port-forward svc/kps-grafana 13000:80'
```

then open <http://localhost:3300>. The user is `admin`; the password is in `~/monitoring-work/grafana-login.txt` on the
server (`ssh YOUR-LOGIN@YOUR-SERVER 'cat ~/monitoring-work/grafana-login.txt'`). Dashboards are in the folder **Hospital PM**.
Prometheus and Alertmanager the same way (`svc/kps-prometheus 9090`, `svc/kps-alertmanager 9093`).

| Dashboard | Shows |
|---|---|
| **Backend** | requests per second by outcome, p50/p95/p99, busiest and slowest routes, 5xx, refused saves, database connection failures and timeouts, process memory and CPU |
| **PostgreSQL** | up, connections by state and against `max_connections`, cache hit ratio, transactions, rows, locks, deadlocks, database size over time |
| **Infrastructure** | node ready, CPU, memory, disk used and left by filesystem, network, disk I/O, container CPU and memory by namespace, pod restarts |
| **Kubernetes** | desired against available replicas, pods pending or failed, restarts, CPU and memory requested against used, node pressure |

The Kubernetes chart also ships its own dashboards (Kubernetes / Compute Resources, Node Exporter, and more) beside these.

## 7. Alerts

18 rules in `prometheus/rules.yaml`, beside the ones the chart ships. Every threshold has its reason beside it in the file.

| Alert | Fires when | Severity |
|---|---|---|
| HospitalPmBackendDown | the application's numbers cannot be read for 2 min | critical |
| HospitalPmHighServerErrorRate | over 5% of requests are 5xx for 5 min (with over 0.5 requests/s) | critical |
| HospitalPmSlowResponses | p95 over 2 s for 10 min, PDFs and exports left out | warning |
| HospitalPmDatabaseUnreachable / DatabaseTimeouts | the application could not reach, or waited too long for, the database | critical / warning |
| HospitalPmMemoryNearLimit | over 90% of its 1 GiB limit for 10 min | warning |
| PostgresDown | the exporter cannot reach PostgreSQL for 2 min | critical |
| PostgresConnectionsNearLimit, PostgresDeadlocks, PostgresCacheHitRatioLow | over 80% of `max_connections`; any deadlock; under 90% reads from memory | warning |
| ServerCpuHigh, ServerMemoryHigh | over 85% / 90% for 15 min | warning |
| ServerDiskLow / ServerDiskCritical | under 15% / 5% free on a real filesystem for 10 min | warning / critical |
| HospitalPmWorkloadUnavailable, HospitalPmPodRestarting | replicas not ready 5 min; over 3 restarts in 30 min | warning |
| PrometheusStorageNearRetentionLimit, PrometheusConfigReloadFailed | over 90% of the 25 GB cap; a bad rule | warning |

The chart's own rules cover a target down, a node not ready, crash loops and filling volumes. Thresholds are first
guesses from the 11 October stress test: keep a week of real figures, then tune them.

### Alert delivery: NOT set up yet

Alertmanager routes everything (except the always-on `Watchdog`) to `alert-sink`, a **test receiver that only logs**. This
was tested: alerts did leave Alertmanager and arrive (`kubectl -n monitoring logs deploy/alert-sink`). **Nobody is told.**
To tell a person, add a receiver for a channel you have credentials for, for example email:

```yaml
# in kube-prometheus-stack-values.yaml, alertmanager.config.receivers, and change route.receiver to it
- name: email
  email_configs:
    - to: you@example.org
      from: alerts@example.org
      smarthost: smtp.example.org:587
      auth_username: alerts@example.org
      auth_password_file: /etc/alertmanager/secrets/smtp/password   # from a Secret, never typed here
```

Create the Secret (`kubectl -n monitoring create secret generic smtp --from-literal=password=...`), list it under
`alertmanager.alertmanagerSpec.secrets: [smtp]`, `helm upgrade`, then send a test alert and **look at the inbox** before
believing it works. WhatsApp needs a separate, authorised provider and is not part of this.

## 8. Storage and resources

| Volume | Size | Why |
|---|---|---|
| Prometheus | 30 Gi (cap 25 GB, 30 days) | about 40 000 series at a sample every 30 s is roughly 170 MB a day, so about 5 GB for 30 days: this is six times that, for a load test and growth. **Check against the real figure after a week** (`prometheus_tsdb_storage_blocks_bytes`). |
| Grafana | 2 Gi | its database; the dashboards come from Git |
| Alertmanager | 1 Gi | silences and the notification log |

`local-path` volumes live under `/data/k3s/storage` on the one disk. They survive pod restarts (tested) but **not** the
loss of that disk or `k3s-uninstall.sh`. They are not backed up. Resource limits are in the values files; at rest the
stack used about 1 GiB of memory and under 0.2 CPU.

**Backup and recovery.** Dashboards, rules and every setting are in Git: that is their backup. Only the metric history
is not, and losing it costs history, not function. To keep it: copy `/data/k3s/storage/pvc-*prometheus*` while Prometheus
is stopped, or use Prometheus snapshots (`--web.enable-admin-api`, deliberately not enabled). Hospital PM's own data is a
separate matter: its two-hourly `.dump` backups are in its own volume (see `docs/DEPLOY-DOCKER.md`).

## 9. Security

Done and tested:
- No Ingress, no NodePort and no LoadBalancer for any monitoring part: Grafana, Prometheus, Alertmanager and the
  exporters are reachable only inside the cluster and by `kubectl port-forward` through an SSH login.
- Network policies (tested from a stray pod): only port 5000 of Hospital PM is reachable; the metrics port only from
  Prometheus; the database only from the application and the exporter; the monitoring namespace from itself.
- The database login for monitoring is read-only (`pg_monitor`) and was shown to be refused a read of `equipment` and a
  `CREATE TABLE`. Its password was generated on the server and exists only in a Secret.
- Grafana: login from a Secret, sign-up and anonymous access off, no telemetry, no gravatar, no snapshots to the outside.
- Metrics carry route patterns only (tested: an equipment number and a search typed into a URL never appear).

Not done, or not a guarantee:
- **Outgoing traffic is not restricted.** Add egress rules if the hospital's network needs it.
- Ports 9100, 6443, 10250: closed by `restrict-ports.sh`, which needs `sudo`. Until it is run they are open to the
  server's networks (found from another computer on the Tailscale network).
- Metrics traffic inside the cluster is plain HTTP, and PostgreSQL's connection from the exporter is not encrypted
  (`sslmode=disable`; the database image has no certificate). Both stay inside one machine's pod network.
- The k3s API server and kubelet listen on all interfaces by design; the firewall rule is the only thing closing them.
- This is a test setup on a **shared** server, not a hardened production cluster. Nothing here is a claim that it is "secure".

Firewall for a production site: allow only SSH (and the application's own port); nothing else from this folder needs an
inbound rule.

## 10. Load test

`loadtest/k6-hospitalpm.js` simulates staff: each person signs in once, then does one thing every 3 to 10 seconds
(dashboard, equipment list and search, a machine, work orders, PM tasks, reports; with `WRITES=1` also reports faults).
It runs **inside the cluster**, against the application's own address, as a Job, and writes its figures into Prometheus.

```bash
kubectl apply -f <(sed -n '1,/^---/p' monitoring/loadtest/job.yaml | sed '$d')          # namespace "loadtest"
kubectl -n loadtest create configmap k6-script --from-file=monitoring/loadtest/k6-hospitalpm.js
kubectl -n loadtest create secret generic k6-accounts --from-literal=users='name1,name2,name3' --from-literal=password='...'
sed 's/value: "ramp"/value: "baseline"/' monitoring/loadtest/job.yaml | kubectl apply -f -   # PROFILE: baseline, ramp, soak
kubectl -n loadtest logs -f job/k6-hospitalpm
```

Order: **baseline** (10 people, 5 minutes) to prove the setup and give the yardstick; then **ramp** (10, 50, 100, 250, 500,
750, 1000 people, each held 3 to 14 minutes, then 10 minutes of rest to see recovery); then **soak** (250 people for
two hours) to look for slow leaks. Watch the Backend and PostgreSQL dashboards while it runs. Only ever against a copy
you are allowed to load. The accounts are test accounts; no patient data exists to use.

"1,000 concurrent users" here is 1,000 **people** thinking between clicks (about 150 requests a second). The stress test
in `tools/stresstest` sends flat-out requests and is the harsher measure.

### Results, 12 October 2026 (k3s on the test server, Hospital PM v0.0.10, 583 machines, one application pod, read-only)

**Baseline** (10 people, 5 minutes, run twice): 100% of checks passed, 0 failed requests, 95th percentile 20 ms. The first run
showed 2% "failures": all were the PM compliance report answering **403** to engineer accounts, which is correct. The script
now expects that.

**Ramp to 1,000 people** (50 minutes, then 10 minutes at rest): **passed every threshold.** 217,960 requests, 2,451 sign-ins,
**0 failed requests, 0 server errors, 0 database connection failures, 0 pod restarts.**

| | Measured |
|---|---|
| People at once | 1,000 (each one thing every 3 to 10 s) |
| Throughput at the peak | 156 requests per second (72 per second averaged over the whole run) |
| Reading, 95th / 99th percentile (k6) | 17.8 ms / 22.0 ms |
| Everything except sign-in, 95th percentile at the worst minute (server's own figure) | 29 ms |
| **Sign-in**, 95th percentile at the worst minute | **0.97 s** (slow on purpose: password protection) |
| Application | 1.07 CPU cores and 363 MiB at the peak (limit 1 GiB) |
| PostgreSQL | 0.76 CPU cores, 91 MiB, 11 connections of 100, cache hit 100% |
| Whole server | 34% CPU at the peak (32 cores, shared) |
| After the load | application CPU back to 0.19 cores within minutes; all 15 targets up |

What this does and does not show:
- It shows that **one application pod and one database on this server serve 1,000 people who pause between clicks** with
  large headroom: the application used about one core and a third of its memory.
- It does **not** cover the heavy requests (service-report PDFs, label sheets, the full export). The 11 October stress test
  found those slow (about 25 a second in total, and a full export of 21 s at 80 at once) and that very long text in lists
  can use all the memory; neither is in this script. Run it with `WRITES=1` and add those if the pilot will use them.
- It does **not** show 1,000 people sending requests flat out (that is about 10 times the traffic). `tools/stresstest`
  measures that and found the database's 2 CPUs the limit at roughly 350 reads a second.
- The accounts were five engineers sharing one password. The 403 shows access control holds under load.
- The test server is shared, so another person's work could have changed the numbers either way. Repeat it on the pilot hardware.
- Prometheus's storage-size figure is empty until it has compacted its first two-hour block, so the storage alert and the
  size estimate in section 8 could only be checked after a day.


## 11. Troubleshooting

| You see | Cause and what to do |
|---|---|
| Pods stay `Pending`, node has taint `disk-pressure` | the root disk is nearly full and the kubelet judges by it. `fix-kubelet-root.sh`. |
| `kubectl` says "permission denied" on `/etc/rancher/k3s/k3s.yaml` | use `export KUBECONFIG=$HOME/.kube/config` |
| A target is down after applying the network policies | `kubectl delete -f monitoring/kubernetes/network-policies.yaml`, then read the policy for that pod's labels |
| `HospitalPmBackendDown` right after an upgrade | metrics are off: ConfigMap `hospitalpm-config` needs `METRICS_ENABLED: "true"`, then restart the application |
| A dashboard panel says "No data" | the thing it counts has not happened (no 5xx, no restarts). `check-dashboards.py` lists which are empty |
| Grafana is slow to start | it is waiting for its volume; `kubectl -n monitoring describe pod` |

## 12. What still needs a person

1. **A real alert channel** (section 7). Until then alerts reach no one.
2. **`sudo bash monitoring/k3s/restrict-ports.sh`** on the server (section 9).
3. **The server's root disk** is 99% full. It is mostly other people's files, so it is theirs to clear. The disk alerts will keep firing, correctly.
4. Retune the alert thresholds after a week of real figures, and check the Prometheus size estimate.
