# What is not done yet

Written 5 October 2026, after the encrypted-backup slice. It replaces the open parts of
`PLAN-2026-10-05.md`. One slice at a time: migration → API → UI → tests → commit.

## Status, end of 5 October 2026

| Item | State |
|---|---|
| 0. Commit the encryption slice | **Done and pushed** (37ceba0) |
| 1. Download, restore from a file, photos survive a restore | **Done**, committed locally (3e45852), not pushed |
| 1b. Backups are the Developer's alone | **Done**, committed locally (094de16), not pushed |
| 2. Google Drive with rclone | **Not started.** Needs the Google account and its service-account key from you |
| 3. Licence: equipment cap | **Done** (add and import). Not yet committed |
| 3. Licence: modules | **Not done.** Needs you to say which sections are modules |
| 3. Licence: public key in the Docker builds | **Done in the files** (build fails without it). Images not rebuilt or pushed |
| 3b. Developer licence section, lock and unlock | **Done.** Not yet committed |
| 4. Encrypt photos and PM files at rest | **Not started** |
| 4. Data-drive encryption check | **Not started** |
| 4. PDFs | **Waiting** on your answer: a password, or nothing |
| 5. DevOps (pipeline, K8s, scan) | **Not started** |
| 6. Dev backend container | **Not touched.** Needs your yes to log in to its database |

The sections below are the original plan, kept for the reasoning.

## 0. Commit what is finished (done)

Committed and pushed as 37ceba0.

## 1. Backup that brings everything back (next)

Your three rules: back up the database and the photos, keep every photo linked to its record, skip
generated reports, and let an Admin press one button to get it all back.

- **Already true:** photos and uploaded PM report files are rows in the database, with foreign keys to
  their work order or task. One dump therefore holds data, photos and links together. Reports are drawn on
  request and are never stored, so they are not in the backup.
- **Test to add:** restore a backup into a scratch database and prove every photo and PM file comes back
  byte for byte and still points at the right work order. This is the proof of "the links survive".
- **Download button (Admin):** download the encrypted backup file from the Backups page. It stays
  encrypted, so it is safe to carry.
- **Restore-from-a-file button (Admin):** upload that file and the recovery key, and the system is rebuilt.
  This is the "data is lost, press a button, everything is back" case, and it works on a new machine.
- **The licence file is not in the backup** (decided 5 Oct). A rebuilt machine is licensed again from the
  Developer's Licence section (item 3).
- **Done when** a clean install, a downloaded backup and the recovery key give back every record and photo.

## 1b. Backups belong to the Developer only (decided 5 Oct)

Backups, restore, download, restore-from-a-file, the recovery key and the Google upload belong to the
**Developer** account. Not the IT team, not the Head of Biomedical.

- Remove `system.backups` and `system.restore` from the IT team's role, and from the Head's by name.
- They can no longer be given by a per-person grant on the Access page. A permission nobody but the
  Developer may hold, and a test that says so.
- The Backups page and its menu entry disappear for everyone else. The route matrix test is updated.
- The hospital cannot restore by itself. That is the price of the choice: a restore is a call to us.
  The Backups page said "Admin" before; it now says Developer.
- The nightly backup still runs and still writes the encrypted file. Only who may see and use it changes.

## 2. Google Drive for backups (with rclone, your choice of 5 Oct)

rclone is a single MIT-licensed program that copies files to Google Drive (and OneDrive, S3, SFTP, a
network share). We run it like we run `pg_dump`: one command after each backup, then it exits. It is not a
service, so it does not break the two-services rule. It is still a new program to ship, so this needs your
yes on the points below.

- After a backup is written and verified, run `rclone copy` on the **encrypted** file only. Nothing readable
  leaves the building. Retention: `rclone delete --min-age`, or keep the last N.
- Off by default. Air-gapped hospitals never see it. A failed upload never fails the backup.
- **Decided 5 Oct: our own Google account, built in (option B).** Every install uploads to the same Google
  account, which is ours. There is no Google sign-in on the hospital's side, and no rclone token to paste.
- We write the rclone config ourselves from a credential set at **build time** (a CI secret, never in the
  repository). It goes to a private temp config for the run and is deleted after. It is still inside a
  program that is downloadable, so anyone who takes it apart can reach that account. The mitigations I will
  build, so one leak is a small leak:
  - a Google **service account** with its own Cloud project, used for nothing else, rotated by a new release;
  - each hospital uploads to its **own folder**, named by its licence id, and the files are already encrypted
    with that hospital's own keys, so a leaked credential reads nothing;
  - only used while the licence is valid;
  - the Google account has 2-step sign-in and is not our personal one.
- **Open risks I am carrying, not solving:** the credential can delete or overwrite every hospital's backup;
  all hospitals share one storage quota (15 GB free); and we hold hospital data (encrypted), so the hospital
  should be told and agree. I will put one plain sentence on the licence screen and in the guide.
- Sign-in: none for the hospital. The credential is built in.
- Where the program comes from: bundled in the installer as an optional component (about 25 MB more), or
  the admin drops `rclone.exe` in a folder. Bundled is simpler for a hospital with no IT staff.
- Windows antivirus sometimes flags `rclone.exe`. We would ship the official release, and note the checksum.
- Same remote can later be a USB path or a network share with no new code.
- **Still needed from you:** the Google account (a Workspace one, not personal), and a Cloud project with a
  service account. I will not ask you to paste the key into chat: it goes in as a CI secret.
- **Done when** a nightly backup lands on Drive encrypted and restores on a clean install.

## 3. Finish the licence

You paused the licence on 20 September and asked for it on 5 October; I am treating the new request as
current.

- Enforce `MaxEquipment`: refuse the machine over the cap, on add and on import, with a plain message.
- Enforce `Modules` for the sections you name. **You tell me which sections are modules.**
- Put the licence public key into the Docker build, and fail the build without it. Today the images I
  pushed to Docker Hub run unlicensed.
- Test a renewal and an expiry end to end.
- Keys and secrets already exist. Do not regenerate them.

### 3b. A Licence section for the Developer: issue, lock and unlock (decided 5 Oct)

Today a licence is made on the command line with `tools/HospitalPm.LicenceTool`. This moves that into the
product, for the Developer role, and adds a lock.

- **Where it runs.** Only on **our own copy** of the software, the one that can see the signing key. The
  signing key never goes onto a hospital machine, so a hospital's copy shows no such section, even to a
  Developer account signed in there. The page reads the key from a path in our local settings and refuses
  to run without it.
- **Issue and renew:** a form for hospital name, days or end date, equipment cap and modules. It makes the
  signed licence text, ready to send. A list of every licence issued, with its id, hospital, dates and notes
  (kept in our own database, not the hospital's).
- **Lock and unlock:** pick a licence from the list and press Lock or Unlock. The page makes a short signed
  **code**. You send it by WhatsApp or email and their IT pastes it. Delivery is by pasted code only, so it
  works on an offline machine. It is not instant.
- **What Locked means: full lock-out** (your choice). Nobody signs in and no record opens until an unlock code
  is pasted. This differs from today's expiry, which becomes read-only. The risk I am carrying, said once: a
  hospital could be unable to reach a machine's service history during a dispute.
- **How a locked install is unlocked.** Nobody can sign in, so the lock screen has its own box for a pasted
  code. It accepts only a code that verifies against the built-in public key and names this install's
  licence id. Nothing else is reachable from that screen. It shows the hospital name and licence id so they
  can quote it when they ring.
- **A code cannot be replayed or undone.** Each code carries a number that only goes up per licence, so an
  old *unlock* cannot undo a newer *lock*, and the other way round. Built differently from this plan: the
  highest number seen is stored in **two files** (beside the licence, and in the data folder's `keys` folder),
  not the database. Either file restores the other. Restoring an old backup therefore cannot unlock a locked
  install, because neither file is in the database.
- **Limits, said plainly.** A licence file is not tied to a machine, so a hospital with no licence file
  ("unlicensed", a pilot) has no id to lock. Someone with administrator rights on the machine can still
  delete the state file and the database rows. This stops casual cases, not a determined one. It does not
  phone home, so a hospital that never pastes the code is never locked.
- **Tests:** a lock code locks and blocks sign-in; a code for another licence is refused; an old unlock does
  not undo a newer lock; restoring an old backup does not unlock; a tampered code is refused; the Developer
  section is absent without the signing key and for every other role (route matrix).
- **Done when** a licence issued from the page installs on a clean machine, a lock code stops all sign-in,
  and an unlock code pasted on the lock screen restores it.

## 4. Encrypt what is stored

- **Photos and PM files:** encrypt the bytes at rest with a per-file nonce and a key version. Migrate what
  exists. Take a backup first, because migrations only go forward.
- **Database files:** PostgreSQL cannot encrypt its own files. Add a diagnostics check that says whether
  the data drive is encrypted, and a short BitLocker / LUKS guide. No claim beyond that.
- **PDFs:** they are never stored. **Decision for you:** a password on a PDF that is emailed out (needs a
  PDF library, MIT-licensed only), or nothing, since there is nothing at rest.

## 5. DevOps

- Rebuild and re-push both Docker images. The ones on Docker Hub predate encryption and carry no licence key.
- A pipeline that builds both images, runs the container check, and pushes on a version tag using a Docker
  Hub **access token** held as a secret, not your password.
- Compose file and Kubernetes files for backend plus PostgreSQL, the password in a Secret.
- Fixed version tags and an image scan.
- Kubernetes stays out of the product (CLAUDE.md bans it for the client install). These files are only for
  the hospital that already runs a cluster.

## 6. Loose ends and known gaps

| Item | State |
|---|---|
| Restore button on an **installed Windows** machine | Not run end to end. There is no installed PostgreSQL here. I will test on a clean Windows install |
| `pg_restore -j` (parallel) on an encrypted backup | Not supported. Restore is single-threaded. Fine for this size |
| Recovery-key creation in the audit log | Only in the app log. The audit table is trigger-only, so it cannot be written from code. Needs a design |
| Keys in Docker / compose | Keys and backups share one volume. Say so in the guide, or use a second volume |
| Dev backend container (`4d182c81…`) | Has no connection string. Needs your yes to log in to the database and recreate |
| Dev API and web | Stopped. I start them when you ask |
| Two dev audit rows holding hashes | Left as they are, by your decision |

## Order I would work in

1. Commit (item 0).
2. Download, Restore-from-file and the link test (item 1).
3. Google Drive (item 2).
4. Equipment cap, then modules, then the Docker key (item 3); then the Developer Licence section with
   issue, lock and unlock (item 3b).
5. Encrypt photos, the disk check (item 4).
6. DevOps (item 5), then the loose ends.

## Questions I need answered

1. Commit and push item 0 now?
2. ~~Licence file inside the backup~~ No. Answered.
3. Which Google account, and who owns the Cloud project? And rclone: bundled in the installer, or placed by the admin?
4. Which sections count as licence modules?
5. PDF: password, or nothing?
6. Is it all right to fix the dev backend container?
