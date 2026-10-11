# Stress test: where does it break?

Vendor-side, like the other tools: CI never publishes `tools/`. This is different from `tools/loadtest`, which times
every endpoint with one user and a very large register. This one sends **many users at once** and keeps adding more
until something fails, then says what failed first and whether the app recovered.

## Run it

1. `bash tools/stresstest/local-stack.sh up` makes an isolated app and database (every name starts `stress-`), limited
   to 2 CPUs and 1 GB each, like a modest hospital PC, and fills the demo hospital.
2. `node tools/stresstest/stress.mjs --url http://127.0.0.1:5600 --user admin --password 'Stress-Admin-1' --containers stress-app,stress-db`
3. `bash tools/stresstest/local-stack.sh down` removes it all.

Node 18 or newer, no packages.

## What it does

Stages of simultaneous users (default 10, 25, 50, 100, 200, 400, 800, 1500, 3000), 20 seconds each, every user
sending requests back to back. For each stage: requests per second, failures by kind (timeout, connection reset,
HTTP 5xx, HTTP 4xx), latency p50/p95/p99/max, the health check (its own connection, once a second) and, with
`--containers`, the peak CPU and memory of those containers. It stops at the first stage where more than 5% fail,
the 95th percentile passes 10 seconds, or the health check fails more than it succeeds. Then it removes the load,
waits, and checks the app comes back.

| `--scenario` | What each user does |
|---|---|
| `read` | lists, searches, details, the dashboard, reports |
| `heavy` | PDFs (service report, PM certificate, label sheet) and the full export |
| `write` | reports faults, takes them, adds notes, sends bad and oversized input (**creates data**) |
| `login` | signs in over and over (password hashing is slow on purpose) |
| `mixed` | all of the above (default) |

Useful runs:

```bash
node tools/stresstest/stress.mjs ... --scenario read  --stages 50,200,800,3000
node tools/stresstest/stress.mjs ... --scenario heavy --stages 5,10,20,40,80
node tools/stresstest/stress.mjs ... --stages 200 --stage-seconds 900     # a 15-minute soak: does it leak?
node tools/stresstest/stress.mjs ... --keep-going                         # carry on past the first break
```

## Only against your own throwaway copy

It is built to hurt. Do not point it at a hospital's installation, or at a server that shares a disk or memory with
anything you care about. `write` fills the database. A report goes to `tools/stresstest/results/` (not committed).
