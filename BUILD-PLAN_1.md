# Hospital PM Software — Build Plan

**Product:** Biomedical equipment preventive-maintenance and asset management system for Indian hospitals.
**Builder:** Solo, with Claude Code, plus paid reviews at two checkpoints.
**Target:** First paid install ~8 months from start.

---

## The two constraints that govern every decision

1. **It must install in five minutes on a hospital's own machine** — either a Windows PC with no IT staff, or a Linux server. One binary, two services, no internet required.
2. **It holds no patient data.** Equipment, work orders, engineers, contracts. This removes an entire compliance burden most "hospital software" carries. Don't reintroduce it by accident.

Every architecture question gets settled by asking whether the answer still installs in five minutes and still runs air-gapped.

---

## Timeline at a glance

| Phase | Weeks | Outcome |
|---|---|---|
| 0. Foundations | 1–3 | Repo, schema, auth, CI, migrations working |
| 1. Core slices | 4–13 | Demoable product, end to end |
| 2. Packaging | 14–17 | Real installer, real install on someone else's machine |
| 3. Design partner pilot | 18–25 | Running free in one real hospital |
| 4. Harden and sell | 26–33 | First paid install |
| 5. Modules | 34+ | Escalation, spare parts, AMC, NABH pack |

Sales activity runs from **week 1**, not week 26. See the parallel track below.

---

## Phase 0 — Foundations (weeks 1–3)

The point of this phase is that nothing after it needs re-doing.

**Week 1**
- Repo, GitHub Actions matrix build for `win-x64` and `linux-x64`, self-contained single-file publish working from day one. If the build only produces a Linux binary now, you'll discover the Windows problems in month six.
- `CLAUDE.md` written before any feature code. Contents listed at the end of this document.
- Solution structure: API, Domain, Infrastructure, Web (React), Mobile (Expo).

**Weeks 2–3**
- **Schema.** Equipment, location hierarchy (org → site → building → floor → department → room), equipment types, checklist templates as JSONB, PM schedules, work orders with the full state machine, escalation threads, users and roles, audit tables.
- Every table gets `tenant_id`, even though it's always 1 for now.
- Audit tables written by Postgres triggers, append-only, no update or delete grants.
- EF Core migrations, forward-only, auto-run on service start.
- Auth: Identity + JWT + refresh tokens, RBAC for Admin / Biomedical Head / Senior Engineer / Technician.
- Testcontainers wired up so integration tests hit real Postgres.

**Gate — do not proceed until:**
- A clean machine can `git clone`, build, and produce both binaries.
- Migrations run from empty to current in one step.
- An audit row appears automatically when a record changes, without application code writing it.

**Paid review #1 here.** Two to three days of a senior .NET contractor on the schema, auth, and tenancy model. ~₹40,000. This is the cheapest possible moment to find out something is wrong.

---

## Phase 1 — Core vertical slices (weeks 4–13)

Build each slice all the way through — migration, API, web screen, mobile screen if relevant, tests — before starting the next. Commit after every working slice.

| Weeks | Slice |
|---|---|
| 4–5 | Equipment master: CRUD, location assignment, Excel import, search and filter |
| 6 | QR generation, asset tag layout, ZPL printing, mobile scan → equipment detail |
| 7–8 | Checklist templates per equipment type, versioned so old completions stay readable |
| 9–10 | PM scheduler: frequencies, due-date generation via Hangfire, due/overdue/completed states |
| 11 | PM execution on mobile: scan, complete checklist, signature, submit |
| 12 | Breakdown tickets and work orders, assignment, resolution |
| 13 | PDF service report and PM certificate via QuestPDF; dashboard |

**Excel import deserves more time than it looks.** Every hospital's first move is handing you a messy asset spreadsheet. Import quality is a sales asset, not a chore.

**Offline scope — keep it small.** Opening a work order requires connectivity. Writes queue locally and replay in order if signal drops mid-task. Do not attempt bidirectional sync with conflict resolution. It is the single most likely thing to consume two months and still be subtly broken.

**Gate — do not proceed until:** you can walk into a room with a phone and a laptop, scan a tag, complete a PM, and print a service report a hospital would accept. Demo it to a biomedical engineer who doesn't work for you.

---

## Phase 2 — Packaging (weeks 14–17)

This is your actual differentiator and it is real engineering, not a build step.

- Inno Setup installer bundling PostgreSQL on a non-standard port, registering both Windows Services.
- Installer handles: firewall rule, antivirus exclusion for the Postgres data directory, spec check, clean uninstall that preserves data by default.
- Silent install flags with a config file, for SCCM/Intune.
- Setup wizard: hospital name, admin account, license file. Final screen prints the mobile-config QR.
- mDNS so devices resolve `hospitalpm.local` and a DHCP change never becomes a support call.
- Diagnostics page: database, disk space, last backup, license status, phone connectivity.
- Nightly `pg_dump` on a Hangfire job, backup status visible on the dashboard.
- Signed license file with offline public-key verification, carrying module flags and expiry.
- Docker Compose for the Linux target.
- OV code signing certificate purchased and applied.

**Gate:** someone who is not you installs it on their own Windows machine, from a downloaded installer, with no instructions from you, and it works. Test on Windows 10 Home with an antivirus running. Test with the network cable unplugged.

---

## Phase 3 — Design partner pilot (weeks 18–25)

One hospital, running free, in exchange for access and honesty.

- Install in their biomedical department. Load their real asset list.
- Sit with technicians for a full day in week one. Watch, don't explain.
- Weekly release cadence against what you observe.
- Track: assets loaded, PMs completed in-app vs on paper, scan failures, support calls, and the reasons for each.

The purpose is not validation. It's finding the twenty small things that make technicians abandon software, which you cannot predict from a desk.

**Gate:** technicians use it without being told to, for four consecutive weeks, and the biomedical head can produce a PM compliance report from it that they'd show an auditor.

---

## Phase 4 — Harden and sell (weeks 26–33)

- Fix everything the pilot surfaced.
- Performance test at 15,000 assets and 50,000 work orders. Fix the queries that fall over.
- **Paid review #2:** security and architecture, ~₹40,000. Do this before your first hospital's VAPT, not after.
- Pricing, licence agreement, MSA, support SLA — get these drafted properly.
- Sales collateral: one-pager, demo video, pilot case study with real numbers.
- Close the first paid install.

---

## Phase 5 — Modules (week 34 onward)

Built as licence flags in the same binary, never as separate builds. Order by what paying customers ask for, not by this list:

Escalation & remote diagnostics · Spare parts inventory · AMC/CMC contracts · NABH audit pack · Multi-site rollup · WhatsApp/SMS (recurring charge, never one-time) · HIS integration & SSO (quoted per hospital)

---

## Parallel sales track — starts week 1

The biggest failure mode available to you is twelve months of building followed by the start of selling. Avoid it by running these from the beginning:

- **Weeks 1–6:** Talk to 15 biomedical engineers and heads. Not selling — asking how they currently track PM, what their last NABH audit was like, what they hate about their existing system. This directly shapes Phase 1.
- **Weeks 6–13:** Identify and secure the design partner. Best qualifier is not bed count, it's **a hospital in an active NABH accreditation or renewal cycle.** Their urgency is real and their budget is unlocked.
- **Weeks 13–25:** Build a pipeline of 10–15 qualified hospitals while the pilot runs. Corporate hospital cycles run 6–18 months, so this must start before the product is finished.
- **Throughout:** Content. You already know how to do this and your competitors are biomedical engineers who don't. NABH equipment-chapter explainers, PM checklist templates, calibration guides. It builds pipeline and it is nearly free.

---

## Budget (₹5–10 lakh over the year)

| Item | Estimate |
|---|---|
| Claude Code subscription | ₹60,000 |
| Paid reviews (2 × 3 days, senior .NET) | ₹80,000 |
| OV code signing certificate | ₹25,000 |
| Cloud VPS (dev + demo) | ₹40,000 |
| Label printer + polyester tag samples (demo kit) | ₹35,000 |
| WhatsApp BSP onboarding + DLT registration | ₹25,000 |
| Legal — licence agreement, MSA, support SLA | ₹50,000 |
| Design — UI polish, brand, sales collateral | ₹75,000 |
| Travel, hospital meetings, demos | ₹50,000 |
| **Subtotal** | **₹4,40,000** |
| Contingency / part-time contractor if you stall | ₹2,00,000+ |

Infrastructure is not your cost. Time is. The contingency line exists so that hitting a wall in month five doesn't kill the project.

---

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| The 70% trap — fast demo, slow production | High | Phases 2 and 4 are explicitly budgeted for hardening, not treated as polish |
| Offline sync consumes two months | High | Scope reduced to a write queue; do not expand it |
| Scope creep from the full feature list | High | Modules are Phase 5. The list is a roadmap, not a v1 spec |
| Long corporate sales cycle burns the runway | High | Parallel sales track from week 1; qualify on NABH cycle, not bed count |
| Can't security-review your own AI-written code | Certain | Two paid reviews, non-negotiable |
| Installer breaks on an unfamiliar Windows machine | High | Phase 2 gate requires a third-party install with no help |
| Incumbent CMMS already in place at large hospitals | Medium | Lead with NABH evidence generation and technician usability, not feature count |

---

## Rules for working with Claude Code

Put these in `CLAUDE.md` at the repo root, where they get read at the start of every session.

**Hard constraints:**
- Single self-contained binary. Windows and Linux. No runtime dependency on the target machine.
- Maximum two services on a client install: the app, and PostgreSQL.
- Must run fully air-gapped. Core loop never assumes internet.
- Banned without explicit discussion: Redis, Chromium/Playwright, MinIO, Kubernetes, any cloud-only managed service, any new background daemon.

**Data rules:**
- `tenant_id` on every table.
- Audit tables are append-only and written by triggers, never by application code.
- Migrations are forward-only and idempotent. Every schema change gets one.
- Checklist definitions are versioned; completed checklists must stay readable against the version they were filled under.

**Working style:**
- One vertical slice at a time: migration → API → UI → tests → commit.
- Commit after every working slice. `git reset` is the recovery path when a change goes wrong.
- No new NuGet or npm dependency without justifying it against the packaging constraint.

---

## Start here

1. Repo + CI producing both binaries.
2. `CLAUDE.md`.
3. Schema.
4. Book the first five conversations with biomedical engineers.

Items 3 and 4 happen in the same week. That's the whole discipline of this plan.
