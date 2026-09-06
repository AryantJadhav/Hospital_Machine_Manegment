# Hospital PM — orientation for a reviewer

This is written for someone paid to spend a few days finding what is wrong
with this codebase. It is not a sales document and not a tutorial. It says
what the system is, what holds it up, and — in the last third — where I think
the weaknesses are, so that time is spent arguing with the decisions rather
than rediscovering them.

Written by the sole author. There has been no outside review of any of it.

---

## 1. What it is, in one paragraph

Biomedical equipment preventive-maintenance and asset management for Indian
hospitals. A hospital installs it on their own PC. It holds their equipment
register, generates PM schedules, records completed checklists with a
signature, and tracks corrective work orders. It is sold as a licensed
product, not a service — we do not host it, and most installs will never be
reachable from the internet.

## 2. The two constraints that decide everything

Every architectural question in this repo resolves to one of these. If a
proposal fails either, it is the wrong proposal, however good it is otherwise.

**It must install in five minutes on a hospital's own machine.** A Windows PC
with no IT staff. One binary, two services, no internet needed. There is no
"install .NET first", no Node on the server, no container runtime. PostgreSQL
is bundled inside the installer and set up without asking the operator a
single question about it.

**It holds no patient data.** Equipment, work orders, engineers, contracts,
locations. Equipment is linked to a *location* — department, room — never to a
person. This removes an entire compliance burden, and it is worth checking
that I have not reintroduced it by accident somewhere. There is no column,
table, or payload field anywhere holding a patient name, ID, MRN, or any
clinical detail; if you find one, that is a serious finding and I want to hear
about it first.

The banned-without-discussion list follows from the first constraint: Redis,
Chromium/Playwright, MinIO, Kubernetes, any cloud-only managed service, any
additional background daemon. `CLAUDE.md` at the repo root is the working
version of these rules.

## 3. Shape of the code

| Project | What lives there |
|---|---|
| `HospitalPm.Domain` | Entities and enums. No EF, no ASP.NET. |
| `HospitalPm.Infrastructure` | EF Core, migrations, PDF, import, backup, licensing. |
| `HospitalPm.Api` | Minimal-API endpoints, auth wiring, hosting, `Program.cs`. |
| `web` | React + TypeScript. Built into `wwwroot` and embedded in the binary. |
| `tests/HospitalPm.IntegrationTests` | xUnit against real PostgreSQL via Testcontainers. |
| `tools/HospitalPm.LicenceTool` | Vendor-only key generation and signing. Never shipped. |
| `installer` | Inno Setup script, the PowerShell it drives, and the smoke test that installs it for real on a CI runner. |

136 C# files (37 of them migrations and their designer files), 18 migrations,
21 TypeScript files. `dotnet test` reports 253 tests from 229 `[Fact]` and
`[Theory]` methods, and needs Docker.

The React bundle being embedded in the binary is what makes "one binary"
literally true — there is no second deployment step and no web server to
configure.

## 4. The load-bearing decision: invariants live in PostgreSQL

This is the design choice I would most like challenged, and the one that most
of the rest depends on.

Rules that must never be violated are enforced by database triggers, not by
application code. Application code can be bypassed by a support engineer with
`psql`, by a future endpoint that forgets a check, or by a background job. A
trigger cannot be, short of deliberately dropping it — which is a visible act
that leaves its own trace.

A live database carries 75 triggers backed by 16 functions — many of them
applied to a list of tables in a loop, so counting `CREATE TRIGGER` in the
migrations undercounts badly. The ones that matter:

**Append-only audit.** `audit_log` rejects `UPDATE`, `DELETE` and `TRUNCATE`
from a trigger, *and* has those privileges revoked from `PUBLIC`. Both,
deliberately: `REVOKE` alone is not enough because the application may connect
as the table owner, and owners bypass table privileges. Audit rows are written
by triggers on the audited tables — never by application code. **If you find
application code writing an audit row, that is a bug and I want to know.**

**Published checklists are frozen.** A completed checklist has to stay
readable, years later, against the version it was filled under. A published
`checklist_template_version` rejects changes to its questions, its version
number, its template, and its publish time, refuses to move back to an earlier
status, and cannot be deleted. New content means a new version.

**Signed PM completions are immutable.** A completion is a signed record;
it cannot be updated or deleted. The remedy for a wrong one is to repeat the
PM, not to edit history.

**Closed work is closed.** A `pm_task` that has closed cannot have its outcome
changed or be deleted. A work order has its state transitions checked in the
database, and once terminal its record cannot be rewritten. Work order notes
are append-only. Work order numbers are allocated by a trigger, not by the
application.

**Location tree integrity.** Locations use a materialised path maintained by
triggers. A location cannot be its own parent or its own ancestor, reparenting
rewrites descendant paths, and equipment cannot point at a location that does
not exist.

`updated_at` is maintained by the database so application code cannot forget
it.

**Where the rule leaks.** Not every invariant could be pushed down. "At most
one primary category per equipment type" is a unique index; "at least one" is
not expressible as a row constraint, because the equipment-type row exists
before any link row does. That half is enforced in application code and
covered by tests — which is exactly the weaker guarantee this whole section
exists to avoid. It is the clearest example of the boundary, and worth
checking whether there are others I did not notice.

Migrations are forward-only, idempotent, and run automatically on service
start. There are no down-migrations. A hospital has no DBA, so the service
brings its own schema up to date or refuses to serve.

**What to attack here:** whether trigger-enforced invariants are testable and
debuggable enough for a contractor to maintain in three years; whether the
error messages surface usefully through EF; whether I have put business
*policy* (which should be changeable) into the database alongside business
*invariants* (which should not).

## 5. Authentication and tenancy

JWT, local issuer and audience — there is no external identity provider,
because the install has to authenticate with the network cable out. Access
tokens last 15 minutes, refresh tokens 14 days. Validating an access token
deliberately does not touch the database, which means a deactivated user keeps
working until their token expires; 15 minutes bounds that window and the
refresh token is where revocation actually bites. Four roles: `Admin`,
`BiomedicalHead`, `SeniorEngineer`, `Technician`.

The signing key is generated per install into a file, not a database row —
the app must validate tokens during startup and while the database is
unreachable, and a key living in the database it protects is a bootstrapping
problem. It is stored in a folder the installer locks to `SYSTEM` and
`Administrators` before anything is written into it.

That location is the result of a bug worth knowing about: an install test
found the key in `C:\Program Files\Hospital PM\data`, where `BUILTIN\Users`
has read access by inheritance. Every local user on a shared ward PC could
read the key that signs authentication tokens, and anyone who can read it can
mint a token for any user, including an administrator. A ward PC with many
Windows logins is exactly the case where that matters. **I would like the
whole key-handling path re-examined by someone who did not write it.**

**Tenancy is schema-only.** Every table carries `tenant_id`, always `1`, and
unique indexes are tenant-scoped (`ux_equipment_tenant_asset_tag` and so on).
Carrying an unused column is far cheaper than retrofitting tenancy later.
**But there is no global query filter and no code that enforces the boundary.**
Today that is harmless — there is one tenant. The day there are two, every
query in the system is a cross-tenant leak until something enforces it. I
consider this a known, deliberate debt rather than a defect, and I would like
a second opinion on whether deferring it this far is defensible.

## 6. Offline scope — deliberately small

Opening a work order requires connectivity. Writes queue locally and replay in
order if signal drops mid-task. That is the whole of it.

There is **no bidirectional sync and no conflict resolution**, on purpose.
General sync is the single most likely thing to consume two months and still
be subtly broken. If reviewing suggests the offline story is inadequate, the
answer I want is "here is why the narrow version fails for a real hospital",
not "add sync".

Mobile is on hold. The PC app and server are the focus.

## 7. Licensing

Offline public-key verification: ECDSA P-256 with SHA-256, BCL only, no
dependency. The licence payload is stored as opaque base64 and signed as exact
bytes, which sidesteps JSON canonicalisation entirely — there is no way for a
re-serialisation to change what was signed.

Key generation and signing live in `tools/HospitalPm.LicenceTool`, which CI
never publishes. The shipped binary therefore cannot mint a licence.

Nothing stops working without a licence today. That is a product decision, not
an oversight, and it is worth telling me if it is the wrong one. `Licence`
appears in exactly two places in the API — its own endpoints and DI
registration — so there is no gate on anything else to review.

**The licence system is currently inert, and knowingly so.**
`Licence:PublicKey` is empty in `appsettings.json`, nothing in CI injects it,
and no production signing key has been generated yet. `LicenceService.Current()`
detects the empty key and reports *"This build has no licence key configured,
so licensing is not enforced"* rather than reporting every licence as forged —
which is the right behaviour, but it means the verification path has only ever
run against test keys. Two consequences worth weighing:

- Generating the production key is a one-way door. If `signing-key.pem` is
  ever lost, every licence already issued becomes unverifiable, and there is
  no recovery path by design.
- The release process that injects the public half **now exists and is worth
  reviewing** — see below. What does not exist is the key itself.

**The release path.** `installer/build.ps1` takes `-LicencePublicKey` (or
`HOSPITALPM_LICENCE_PUBLIC_KEY` from the environment) and **refuses to build
without it** unless `-Unlicensed` is passed explicitly. That default is the
whole point: an installer built with no key works perfectly and reports
"licensing is not enforced", so a release cut without one looks entirely
normal until a hospital is sent a licence their copy cannot check. The failure
had to be made loud because it is otherwise silent.

The key is checked for shape before anything is built — a P-256
SubjectPublicKeyInfo is exactly 91 bytes beginning `0x30` — which catches the
realistic failure of a secret that was truncated or wrapped in transit. It is
then written into the *published* `appsettings.json` (never the one in source,
so a release never dirties the working tree) and read back before the
installer is compiled, because that is the last moment it can be confirmed.

`.github/workflows/release.yml` cuts a release on a `v*` tag: it fails
immediately if the `LICENCE_PUBLIC_KEY` secret is absent, passes it through
the environment rather than a command line, runs the full installer smoke test
against the artifact that is actually about to ship, re-asserts the key is in
the shipped `appsettings.json`, and drafts — not publishes — a GitHub release.
`build.yml` passes `-Unlicensed` explicitly, so the signing key is never
reachable from a pull-request build, including one from a fork.

**What to attack here:** the public key ships in `appsettings.json` inside
Program Files. Standard users cannot write there, but an administrator can
substitute their own key and sign whatever they like. Compiling it into the
assembly would raise that bar; it would not remove it, and nothing currently
stops working without a licence anyway. I think the trade is right for a
product sold to hospitals rather than pirated by them, and I would like that
challenged rather than assumed.

## 8. Backups, restore, diagnostics

Nightly `pg_dump --format=custom` at 02:30 UTC — 08:00 in India, after the
night's PM generation and before the day shift starts writing. Each dump is
read back with `pg_restore --list` to confirm it opens; a dump that has never
been read is not a backup. Retention deletes only files matching
`hospitalpm-*.dump`. The password goes through the child process environment,
never the command line.

Restore runs from a script outside the application, because a process
connected to a database cannot drop and recreate it. It validates the dump
first, stops the app service, takes a `pre-restore-<timestamp>.dump` safety
copy, then drops and recreates — so a restore can itself be undone. It
restores as the database owner rather than as superuser, because restoring as
superuser leaves `permission denied for table __EFMigrationsHistory`.

Proven end to end on a real install: back up with 3 machines, add 2, restore
(3 back, 2 gone), restore from the safety dump (all 5 back), with 75 triggers
and 16 functions intact and `audit_log` still refusing `DELETE` afterwards.

A restored database always carries a backup row still marked `Running` — the
dump was taken mid-run, so the row is frozen that way forever.
`InterruptedBackups.CloseAsync` closes those on start-up and says why. Worth
knowing that this is a *consequence* of restore rather than a separate
feature, because it looks like defensive code with no cause until you hit it.

The `pg_dump` locator examines every candidate on the machine and takes the
first *usable* one. It used to stop at the first one it found, which on a
machine upgraded from PostgreSQL 16 to 17 would have silently stopped backups
forever — found by CI, where a 16 came first on `PATH`.

Diagnostics runs six checks (database, schema, backups, backup tool, disk
space, clock) plus licence, and reports the worst.

## 9. The installer, and a limitation you should know about

Inno Setup, `PrivilegesRequired=admin`, bundled PostgreSQL 17.11 on port 5433
listening on loopback only. The hospital is asked one question — which port —
and finishes with a working login.

**Inno Setup can only fail an installation before file copying begins.** This
was measured against Inno 6.7.3 rather than assumed:

| Failure point | Exit code | Files |
|---|---|---|
| `InitializeSetup` returns False | 1 | none |
| `PrepareToInstall` returns a message | 7 | none |
| `CurStepChanged(ssInstall)` aborts | 3 | rolled back |
| `[Files]` `AfterInstall` raises or aborts | **0** | left installed |
| `[Run]` entry's program exits non-zero | **0** | left installed |
| `[Run]` `BeforeInstall` raises or aborts | **0** | left — *and the entry still runs* |
| `CurStepChanged(ssPostInstall)` raises or aborts | **0** | left installed |

Stage order is `ssInstall` → `[Files] AfterInstall` → `[Run]` → `ssPostInstall`.

Everything that can genuinely fail — creating the cluster, writing settings,
registering and starting the service — happens after that point. An earlier
version of the installer relied on `RaiseException` from a `[Run]` entry's
`BeforeInstall` to stop the install when the database step had failed. It
stops nothing: the exception is logged as an internal error, the `[Run]` entry
executes anyway, and Setup exits 0. A silent install could leave a hospital
with a registered service, no settings file and no database, and report
success.

What the installer does about it now:

- Every check that *can* be made before file copying is made in
  `PrepareToInstall` — app port in use, database port in use, an existing
  cluster whose `db.json` is missing. These exit 7 with a plain-English reason
  and write nothing.
- Everything else runs with its exit code checked, stops at the first failure,
  and writes the reason to `install-failure.txt` **in the install directory**
  (not the data directory, which is locked to `SYSTEM` and `Administrators` —
  a report nobody can open is not a report). The logs it *points* to do stay
  in the locked folder, because `setup-database.log` can capture a failing
  `psql` statement containing the generated database password; the report says
  so, so that the access-denied prompt reads as intended rather than as a
  second fault.
- The last step polls `/health` for up to two minutes. `sc start` returning 0
  only means the service was *asked* to start; it says nothing about whether
  migrations ran or whether the process died two seconds later. All of those
  have happened, and each looked like a clean install.

**So an unattended deployment must test for the file, not the exit code:**

```bat
HospitalPM-Setup.exe /VERYSILENT /PORT=5000 /HOSPITAL="Sahyadri Hospital, Pune" ...
if exist "%ProgramFiles%\Hospital PM\install-failure.txt" ( rem it failed )
```

Values containing spaces must be quoted. Inno reads `{param:}` up to the next
space, so `/HOSPITAL=Sahyadri Hospital` silently becomes "Sahyadri" — and both
a hospital name and a person's name almost always contain spaces. This bites
only unattended installs; the wizard is unaffected.

**If you can find a way to make Inno return a non-zero exit code after file
copying has begun, that is the single most valuable installer finding you
could bring me.**

Other installer behaviour worth knowing:

- `CloseApplications=no`. RestartManager's scan runs before `PrepareToInstall`
  can stop our services, finds them holding our own files, cannot close a
  Windows Service, and asks what to do — which a silent install answers with
  Abort. That is an upgrade failing on exactly the machine that most needs one.
- `PrepareToInstall` **waits** for both services to reach `Stopped`, polling up
  to 90 seconds, rather than sleeping a fixed six. `sc stop` returns as soon as
  it has *signalled* a service, never when the process has gone. The fixed
  sleep was a guess that lost the race often enough to matter: an install test
  caught Setup stopping its own PostgreSQL, waiting six seconds, finding port
  5433 still held by it, and refusing the upgrade with a message blaming
  "another PostgreSQL". The same race previously risked the worse outcome —
  replacing binaries under a live `postgres.exe`. The port checks retry for ten
  seconds too, because a listening socket can outlive the SCM's `Stopped`.
- The uninstaller calls `RemoveDir({app})` explicitly. Inno will not remove the
  install directory once a file it has no record of has lived there, so a
  machine that ever wrote `install-failure.txt` kept an empty `Hospital PM`
  folder in Program Files forever. `RemoveDir` only succeeds on an empty
  directory, so anything a hospital put there by hand survives.
- Uninstalling **keeps** the hospital's data unless `/REMOVEDATA=1` is passed.
  An earlier version asked with a message box defaulting to No, and a
  `/VERYSILENT` uninstall deleted the folder anyway — on a real install, the
  hospital's entire database. A destructive default that only appears when
  nobody is watching is the worst kind.
- The data folder is locked *before* anything is written into it, with no
  `/T`. Locking afterwards with `/T` strips the settings file's inherited ACEs
  while the `(OI)(CI)` container-inheritance grants apply nothing to a file,
  producing a file with an empty ACL that not even LocalSystem can read.
- Both PowerShell scripts clear inherited `PG*` environment variables before
  touching PostgreSQL. **`PGPASSWORD` takes precedence over `PGPASSFILE`**,
  and there is no command-line option for a password — so a machine with
  `PGPASSWORD` set system-wide authenticates as something the installer did
  not choose, however explicit its arguments are. That machine is the one
  that already has PostgreSQL on it, which is precisely the case the private
  port 5433 exists to accommodate. Found by the CI smoke test on its first
  real run: `initdb` succeeded, the cluster started, and the next `psql` died
  with *"password authentication failed for user postgres"* against a
  password the script had just set itself. Everything else (`--host`,
  `--port`, `--username`, `--dbname`) is an explicit connection parameter and
  outranks the environment, so clearing those is hygiene rather than a fix.
- The "Open Hospital PM now" `[Run]` entry carries `skipifsilent`. A
  `postinstall` entry still **executes** under `/VERYSILENT` — the flag only
  governs the checkbox on a Finished page a silent install never shows — so
  without it an unattended install opened a browser, which for an SCCM or
  Intune rollout running as LocalSystem is meaningless at best. Pre-existing,
  and found by the CI smoke test: Setup finished in 26 seconds, `ShellExec`'d
  the URL on a headless runner, and the harness then waited 35 minutes on a
  child process that was never going to exit.
- The settings file binds `http://+:<port>`, not `http://0.0.0.0:<port>`.
  Kestrel binds `0.0.0.0` to IPv4 **only**, while Windows resolves
  `localhost` to `::1` first — and the Start Menu shortcut points at
  `http://localhost:<port>/`. Measured on a real install: **210 ms to connect
  via `localhost` against 0.8 ms via `127.0.0.1`**, on every new connection,
  while the request itself took 4 ms. `+` binds both stacks. This was invisible
  for the whole project because nothing had ever measured a request.
- `install-failure.txt` is deleted in `CurUninstallStepChanged(usUninstall)`,
  not through `[UninstallDelete]`. Inno processes `[UninstallDelete]` entries
  *after* it has already tried to remove the install directory, so the file
  goes but an empty `Hospital PM` folder stays in Program Files. Any file the
  application creates at run time inside `{app}` has this problem.

## 10. Where I think the weaknesses are

Ranked by how much I would like to be wrong about them.

1. **No outside review of anything.** Every design decision here is one
   person's judgement, checked only against itself. This document exists to
   make that cheaper to attack.

2. **The preventive-maintenance workflow has no front end on PC.** The most
   serious finding here, and found late — by walking the product end to end
   at realistic scale rather than slice by slice.

   The web UI can write locations, work orders, backups, the licence and the
   first admin account. It cannot create a checklist template, publish a
   version, create a PM schedule, or complete a PM. Equipment enters *only*
   through the Excel import; there is no way to add a single asset. So the
   loop the product is named for is reachable only by calling the API
   directly.

   `POST /api/pm/schedules` also takes one equipment id per call, so even
   through the API a 2,000-asset hospital faces 2,000 requests. There is no
   bulk path — no "schedule this checklist for every infusion pump".

   PM execution was built for the mobile app, and mobile is on hold, so that
   half currently has no usable client at all.

3. **No hospital has ever used this.** The Phase 1 gate — a biomedical
   engineer who is not me completing a PM round unaided — has never been met.
   I had been treating that as blocked on finding an engineer. Given the item
   above it is also blocked on the software: on the PC app as it stands, the
   round cannot be completed. Every workflow decision remains a guess informed
   by research rather than by watching someone work.

4. **Every install test has been on my machine.** One Windows 11 developer PC
   with McAfee and no Defender. That last part matters more than it sounds:
   the installer's antivirus-exclusion step calls `Add-MpPreference`, so on
   this machine it has silently done nothing every single time. On a hospital
   PC running any third-party AV it will also do nothing, and real-time
   scanning of a PostgreSQL data directory is a known cause of corruption and
   of write stalls that look like the application hanging. **This needs
   testing on Windows 10 Home with a different AV, and probably needs a
   documented manual exclusion step.**

   Related, and observed during this work: McAfee on this machine deletes
   freshly built unsigned Inno installers, non-deterministically, within
   seconds. A hospital downloading an unsigned installer may simply watch it
   vanish. This is an argument for the code-signing certificate that is
   stronger than the SmartScreen warning.

5. **Tenancy is schema-only** (§5). Deliberate, but it is the kind of debt
   that turns into a data-leak incident rather than a refactor.

6. **The installer cannot report post-copy failure through its exit code**
   (§9). Mitigated, not solved.

7. **Restore is destructive by construction.** It is guarded by a typed
   confirmation and a safety dump, and it has been exercised, but it drops a
   database. It deserves a harder look than I can give my own code.

8. **Hangfire runs in-process with one worker**, against the same PostgreSQL,
   to honour the two-services rule. Whether one worker is right when PM
   generation for a large hospital coincides with the nightly backup is
   untested at realistic scale.

9. **Volume is measured now, but only at one size and on one machine.** A
   pass at 2,000 assets across 278 locations — a realistic mid-size Indian
   hospital — found nothing slow: import 2.9s, equipment list 5ms, dashboard
   150ms, a 200-label PDF sheet 3.7s, a full backup 691ms producing a 301 KB
   dump. Nothing there needs optimising.

   What it does *not* cover: 20,000 assets, a year of accumulated PM
   completions and work-order history, or the spinning-disk PC a hospital
   will actually provide. Every number above came from a fast developer
   machine with an empty history table, which is the easiest case there is.

10. **The signing-key path** (§5) — one security bug was already found there by
   accident, which is weak evidence that it was the only one.

11. **The installer's coverage is new and shallow.** The 253 tests stop at the
    API boundary. Every installer claim in §9 was originally established by
    installing on a real machine by hand — better evidence than a mock, and
    evidence that expires the moment someone edits the `.iss`.

    `installer/smoke-test.ps1` now runs those scenarios on a `windows-latest`
    runner on every push: port refusals, orphaned cluster, clean install,
    two consecutive upgrades, a deliberately corrupted cluster, and both
    uninstall paths. A GitHub Windows runner is a throwaway VM whose default
    account is an administrator, which is exactly what registering services
    and locking ACLs needs.

    What it still does not cover: the wizard itself. Every scenario is
    `/VERYSILENT`, so nothing exercises the pages a hospital actually sees,
    the Ready-page memo, or the Finished-page checkbox suppression after a
    failure. It also runs on one Windows version with no third-party
    antivirus, which is the gap in item 3, not this one.

12. **The licence signing key does not exist yet** (§7). The release path that
    injects it now does, and refuses to build without it — but no production
    key has been generated, so verification has still only ever run against
    test keys, and no release has ever been cut through that path.

## 11. Things that are decided, not open

Raising these is fine, but they have been considered and the answer is on the
record:

- **mDNS for discovery** — declined; it would need a new NuGet dependency.
- **Docker Compose** — no Linux customer exists yet.
- **Bidirectional offline sync** — out of scope by design (§6).
- **Mobile app** — built, on hold; PC and server are the focus.

## 12. How to run it

```bash
dotnet test
```

Needs Docker — the tests run against a real PostgreSQL 17 in Testcontainers,
not an in-memory provider. 253 tests, currently all passing.

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

Downloads PostgreSQL against a pinned SHA-256, publishes both binaries, and
compiles the installer.

```powershell
powershell -ExecutionPolicy Bypass -File installer\smoke-test.ps1
```

Installs the compiled installer, breaks it deliberately, and uninstalls it —
eight scenarios, asserting exit codes, `/health`, the SPA, the failure report
and what survives an uninstall. **Destructive.** It registers services, writes
to Program Files and deletes its own data directory, so it is meant for a
throwaway CI runner; it refuses to run at all if Hospital PM is already
installed, rather than eating a real installation.

Verified toolchain on the author's machine: .NET SDK 10.0.400, PostgreSQL
17.11, Node 24.18.0, Docker 29.7.2, Inno Setup 6.7.3.

Credentials are never committed. Local connection strings live in
`appsettings.Development.json`, which is gitignored. The licence signing
private key has never been in the repo and must never be.
