# Load test at hospital scale

Vendor-side, like the demo tool: CI never publishes `tools/`.

Builds a register the size the build plan sets for Phase 4 (15,000 assets,
about 50,000 work orders, a year of PM history: roughly 110,000 PM tasks) and
times every list, search and report endpoint against it.

1. An empty throwaway Postgres 17 and the app pointed at it. Never a live install.
2. Create the first admin, then fill a base hospital:
   `dotnet run --project tools/HospitalPm.DemoData -- --url <url> --password 'Hospital@2026'`
3. Scale it up (audit triggers are switched off for the bulk load only):
   `psql -v ON_ERROR_STOP=1 -f tools/loadtest/seed-scale.sql`
4. `bash tools/loadtest/bench.sh` prints the best of three per endpoint, with size.

Also worth timing by hand at this scale: `POST /api/pm/generate` (the nightly
job) and `POST /api/admin/backups/run`.

Last run (2026-09-20; 15,000 assets, 113k PM tasks, 50k work orders): every
endpoint under 100 ms, generation 7 s in steady state, backup 1.4 s.
