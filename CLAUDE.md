# Hospital PM — working rules

Biomedical equipment preventive-maintenance and asset management for Indian
hospitals. Installed on the hospital's own hardware, not hosted by us.

## The two constraints that settle every architecture question

1. **It must install in five minutes on a hospital's own machine** — a Windows
   PC with no IT staff, or a Linux server. One binary, two services, no
   internet required.
2. **It holds no patient data.** Equipment, work orders, engineers, contracts.
   This removes an entire compliance burden. Do not reintroduce it by accident.

When a design question comes up, ask whether the answer still installs in five
minutes and still runs air-gapped. If it doesn't, it's the wrong answer.

## Hard constraints

- **Single self-contained binary**, `win-x64` and `linux-x64`. No runtime
  dependency on the target machine — no "install .NET first", no Node on the
  server.
- **Maximum two services on a client install**: the app, and PostgreSQL.
- **Must run fully air-gapped.** The core loop never assumes internet. Licence
  verification is offline public-key. No telemetry call on the critical path.
- **Banned without explicit discussion**: Redis, Chromium/Playwright, MinIO,
  Kubernetes, any cloud-only managed service, any new background daemon.

If a task seems to need one of these, stop and raise it rather than adding it.

### No patient data — what this means concretely

Never add a column, table, or payload field holding patient name, ID, MRN,
diagnosis, or any clinical detail. Equipment is linked to a **location**
(department, room), never to a patient. If a feature request implies patient
linkage, flag it before implementing.

## Data rules

- **`tenant_id` on every table.** Always `1` for now. It is far cheaper to
  carry an unused column than to retrofit tenancy later.
- **Audit tables are append-only and written by Postgres triggers**, never by
  application code. No `UPDATE` or `DELETE` grants on them. If application
  code writes an audit row, that's a bug.
- **Migrations are forward-only and idempotent.** Every schema change gets
  one. They auto-run on service start. No down-migrations.
- **Checklist definitions are versioned.** A completed checklist must stay
  readable against the version it was filled under, years later. Never mutate
  a published checklist template in place.

## Offline scope — deliberately small

Opening a work order requires connectivity. Writes queue locally and replay in
order if signal drops mid-task. **Do not attempt bidirectional sync with
conflict resolution.** It is the single most likely thing to consume two months
and still be subtly broken. If a task drifts toward general sync, stop and say so.

## Stack

| Layer | Choice |
|---|---|
| API / Domain / Infrastructure | .NET 10 (LTS), C# |
| Database | PostgreSQL 17 |
| Background jobs | Hangfire (in-process, same binary) |
| PDF | QuestPDF |
| Web | React + TypeScript |
| Mobile | Expo (React Native) |
| Tests | xUnit + Testcontainers (real Postgres, not in-memory) |
| Installer | Inno Setup (Phase 2) |

## Working style

- **One vertical slice at a time**: migration → API → UI → tests → commit.
  Finish the slice before starting the next.
- **Commit after every working slice.** `git reset` is the recovery path when
  a change goes wrong — small commits make it cheap.
- **No new NuGet or npm dependency without justifying it against the packaging
  constraint.** Ask: does this still produce one self-contained binary? Does it
  work air-gapped? Say the justification out loud before adding it.
- Prefer boring, well-supported libraries. This code has to be maintainable by
  a contractor during a paid review, and to run untouched in a hospital for years.

## Local development

Verified on this machine:

- .NET SDK **10.0.400** (`dotnet --version`)
- PostgreSQL **17.11**, service `postgresql-x64-17`, port **5432**
- Node **24.18.0**, npm 11.16.0, pnpm 10.20.0
- Docker **29.7.2** — required for Testcontainers integration tests

Note: MySQL 8.0 also runs on this machine on port 3306. Unrelated to this
project; leave it alone.

**Credentials are never committed.** Local connection strings live in
`appsettings.Development.json`, which is gitignored. Licence signing private
keys never enter the repo.

## Gates — do not skip

Phase 0 is not done until a clean machine can `git clone`, build, and produce
**both** binaries; migrations run from empty to current in one step; and an
audit row appears automatically on a record change without application code
writing it.
