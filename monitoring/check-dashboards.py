#!/usr/bin/env python3
"""
Checks the dashboards the way a person opening them would find out: through Grafana, not around it.

  1. signs in to Grafana (password read from the login file given as argv[1], never printed)
  2. lists the dashboards it provisioned
  3. for every panel of ours, runs the panel's query THROUGH Grafana's data source proxy and reports panels with no data

Run ON THE SERVER, with a port-forward to Grafana on 13000:
    kubectl -n monitoring port-forward svc/kps-grafana 13000:80 &
    python3 monitoring/check-dashboards.py ~/monitoring-work/grafana-login.txt monitoring/grafana/dashboards
"""
import base64
import json
import pathlib
import sys
import time
import urllib.parse
import urllib.request

login = dict(line.split(": ", 1) for line in pathlib.Path(sys.argv[1]).read_text().splitlines() if ": " in line)
auth = "Basic " + base64.b64encode(f"{login['user']}:{login['password']}".encode()).decode()
base = "http://localhost:13000"


def call(path):
    req = urllib.request.Request(base + path, headers={"Authorization": auth})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


print("grafana health:", call("/api/health"))
sources = call("/api/datasources")
prom = next(s for s in sources if s["type"] == "prometheus")
print("data source:", prom["name"], "uid", prom["uid"])

found = call("/api/search?tag=hospital-pm")
print("dashboards provisioned:", sorted(d["title"] for d in found))

now = int(time.time())
empty = []
total = 0
for f in sorted(pathlib.Path(sys.argv[2]).glob("*.json")):
    dash = json.loads(f.read_text(encoding="utf-8"))
    for p in dash["panels"]:
        for t in p["targets"]:
            total += 1
            qs = urllib.parse.urlencode({"query": t["expr"], "time": now})
            try:
                data = call(f"/api/datasources/proxy/uid/{prom['uid']}/api/v1/query?{qs}")
            except Exception as e:  # noqa: BLE001 - report any failure, whatever it is
                empty.append((dash["title"], p["title"], f"ERROR {e}"))
                continue
            if data.get("status") != "success":
                empty.append((dash["title"], p["title"], "query failed"))
            elif not data["data"]["result"]:
                empty.append((dash["title"], p["title"], "no data"))

print(f"{total} queries run through Grafana; {len(empty)} returned nothing or failed:")
for d, p, why in empty:
    print(f"   {d} / {p}: {why}")
