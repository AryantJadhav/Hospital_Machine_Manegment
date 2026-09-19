# Antivirus exclusion for Hospital PM

Written for whoever looks after the PC that Hospital PM is installed on. No
technical background is needed beyond being able to open your antivirus
program's settings.

## Why this is needed

Hospital PM keeps its records in a PostgreSQL database. Antivirus programs that
scan every file as it is written can make a database stall or, in the worst
case, damage its files. The symptom is usually not an error: the app just
becomes slow or appears to hang, sometimes only at busy moments.

The installer adds an exclusion automatically **only if Windows Defender is the
active antivirus**. If the PC runs anything else (McAfee, Quick Heal, Kaspersky,
Norton, ESET, Bitdefender, or a hospital-managed product), the installer cannot
do it, does not report a failure, and **you must add it by hand**. Do this
after installing, and again if the antivirus is replaced.

## What to exclude

Exclude the database folder. Nothing else is required.

| What | Default location |
|---|---|
| Database folder (required) | `C:\ProgramData\Hospital PM\pgdata` |
| Database program (recommended, if your product supports process exclusions) | `C:\Program Files\Hospital PM\pgsql\bin\postgres.exe` |

Exclude both **real-time (on-access) scanning** and, where the product offers
it, **scheduled scans** for the folder. `ProgramData` is a hidden folder: in
File Explorer, turn on *View > Show > Hidden items*, or paste the path into the
address bar.

Do **not** exclude the whole `Hospital PM` folder or the whole drive. Leaving
the program files scanned is intended; only the database needs to be left
alone.

If Hospital PM was installed somewhere other than the default, the database
folder is `pgdata` inside the data folder shown on the installer's final screen,
and `postgres.exe` is under `pgsql\bin` in the program folder.

## How to add it

Every product words this differently. Look in the settings for one of these:
*Exclusions*, *Exceptions*, *Trusted files and folders*, *Real-Time Scan >
Excluded files*, or *Scan exclusions*. Add the folder above, choose the
option that applies to real-time protection, and save.

If the antivirus is managed by the hospital's IT team or a central console, the
setting on the PC may be locked. Send them this page and ask for the two paths
above to be excluded on this machine.

### Windows Defender (only if the installer did not manage it)

Open PowerShell **as Administrator** and run:

```powershell
Add-MpPreference -ExclusionPath 'C:\ProgramData\Hospital PM\pgdata'
Add-MpPreference -ExclusionProcess 'C:\Program Files\Hospital PM\pgsql\bin\postgres.exe'
```

To check what is currently excluded:

```powershell
(Get-MpPreference).ExclusionPath
```

If this prints nothing and Defender is not the active antivirus, that is
expected, because Defender is switched off while another product is installed.
The exclusion has to be made in that other product.

## Check it worked

1. Open Hospital PM and go to the **Diagnostics** page. The database should be
   reported healthy.
2. Record a test PM or open a few pages. Nothing should pause for several
   seconds.
3. If the app was slow before, use it for a day. Slowness that was worst at
   busy moments should be gone.

If the antivirus reports a threat inside `pgdata`, do not delete or restore
anything in that folder. Stop the *HospitalPM* and *HospitalPM_Postgres*
services and contact support, because files in that folder are the equipment
register.

## Also worth knowing

- **The installer itself may be deleted by the antivirus** right after download,
  especially if it is not code-signed. If the file vanishes, that is the
  cause. Ask IT to allow it, or use a signed release.
- Backups are written to `C:\ProgramData\Hospital PM\backups`. These are
  ordinary files and do not need an exclusion.
