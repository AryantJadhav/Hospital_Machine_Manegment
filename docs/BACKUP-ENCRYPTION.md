# Encrypted backups

Every backup is encrypted. A backup is the whole hospital's equipment records in one file, and the file
is the part that leaves the machine: a USB drive, a shared folder, a cloud account. Encrypted, a copy is
useless to whoever picks it up.

This page is for the person who has to get a backup back after the machine is gone.

## The two keys

| Key | Where it is | What it is for |
|---|---|---|
| **Machine key** | On the server, in the locked-down `keys` folder beside the signing key | The server restores its own backups without anyone typing anything |
| **Recovery key** | **Written down, away from the server** | Opens a backup after the server, and its machine key, are gone |

The recovery key is 52 letters and numbers in groups of four, like `WV7H-ICNL-FOVT-7OZ4-…`. Capitals
or lower case, with or without dashes and spaces. A zero typed for an `O`, a one for an `I` or an eight
for a `B` is understood, because the key is written on paper and typed back on a bad day.

**Set it up once, on the Backups page** (Admin ▸ Backups): choose *Create the recovery key*. It is shown
once. Write it down or print it, keep it in a safe or a password manager, away from the server and away
from the backups, and tick that you have. Until you do, the page says so in a warning.

Lose it, or let someone see it who should not have? *Make a new recovery key*. Every backup on the server
is updated to it within seconds, and the old key stops working. (A backup that is not on the server at
that moment, say a copy on a USB drive, keeps the old key. The page tells you how many it updated.)

## Who may do this

Only the **Developer** account. Backups, restore, download and bringing a file in are not held by the
hospital's IT team or the head of Biomedical, and cannot be given to them on the Access page.

## Taking a backup away, and bringing one in

On the Backups page (Developer):

- **Download** on any encrypted backup saves that file to the computer you are using. It stays encrypted. The
  browser saves it straight to disk from a link good for one file and one minute, so a large backup does not
  have to fit in the page.
- **Restore from a file** takes a downloaded file, and the recovery key if it was made on another machine. The
  file is checked first: it must be a whole, untouched Hospital PM backup that opens with this machine's key or
  the recovery key, and the archive inside must open. A file that fails is deleted and nothing is recorded. If it
  passes, it is kept in the backup folder and restored like any other.

What is in a backup: the whole database. Photos and uploaded PM reports are rows in it, each tied to its work order
or PM by a constraint the database enforces, so one file carries the records, the photos and the links, and a restore
returns all three (a test restores a real backup and compares every photo byte for byte). Reports and printouts are
not stored anywhere: the program draws them again from the records, so they are not in the backup.

The licence file is not in the backup. A rebuilt machine is licensed again from the licence section.

## Getting a backup back

### On the server that made it

Backups ▸ **Restore**. Nothing to type but `RESTORE`. The server opens the file with its own key.

### On a new server, or after the machine key is lost

You need the backup file and the **recovery key**.

1. Install Hospital PM on the new machine, so there is a database and the `hospitalpm` program.
2. Turn the backup back into an ordinary PostgreSQL dump:

   ```
   hospitalpm backup-decrypt hospitalpm-20261005-023000-IST.dump.enc restored.dump --recovery-key WV7H-ICNL-FOVT-…
   ```

   On Windows that is `"C:\Program Files\Hospital PM\hospitalpm.exe" backup-decrypt …`, from an
   Administrator command prompt. In a container, `docker exec <container> ./hospitalpm backup-decrypt …`.
3. Restore it with PostgreSQL's own tool, then **delete `restored.dump`**: it is the whole database in
   the clear.

   ```
   pg_restore --clean --if-exists --no-owner --dbname=hospitalpm restored.dump
   ```

The restore page on a *new* Windows install does the same in one step: when a backup will not open with
this machine's key it asks for the recovery key.

If the program says *"That recovery key does not belong to this backup"*, the key written down is not the
one the backup was wrapped with. A backup is opened by the recovery key that was current when it was made,
or by a later one if the server updated it when a new key was made.

## What is protected, and what is not

**Protected:** a backup file that is copied, stolen, left on a USB drive, or uploaded anywhere. Every part
is authenticated, so a flipped byte, a part removed, two parts swapped, a file cut short or bytes added are
refused, never half-restored.

**Not protected:** a key kept on the same drive as the backups. Someone who takes the whole drive, or the
whole data folder, takes the key with it. For the best protection:

- keep the backup folder on a **different drive** from the data folder, or on a drive you carry away;
- **BitLocker** (Windows) or **LUKS** (Linux) on the data drive, which also protects the database itself,
  because PostgreSQL has no encryption of its own files.

The database is not encrypted by this. Only backups are. The **Diagnostics** page has a *Drive encryption* check that
asks the operating system (BitLocker on Windows, LUKS on Linux) whether the drive holding the data is encrypted, and
says "could not tell" when it cannot (a container, no permission to ask) instead of guessing.

## Backups made before encryption was switched on

The first time the new version starts, every plain backup in the backup folder is encrypted, read back and
compared with the original, and only then is the plain file deleted. The Backups page keeps listing it. A
file written in the last half minute is left alone, in case it is still being written.

The safety copy that a restore makes of the database it replaces is plain when the restore script writes it,
and is encrypted the same way when the service comes back up. A restore decrypts the chosen backup into a
folder of its own for the script, and the service empties that folder as it starts.

## Settings

All optional. In `appsettings.json` or the environment (`Backup__Encrypt=false`).

| Setting | Default | |
|---|---|---|
| `Backup:Encrypt` | `true` | `false` writes plain dumps as before. Only for a hospital that encrypts the backup drive itself |
| `Backup:KeyDirectory` | `<data>/keys` | Where the machine key is kept. Best on a different drive from the backups |

## The file

For anyone who has to open one without this program. Version 1, a 169-byte header and then parts:

```
HPBK, version 1, file id, nonce prefix, part size,
machine key id + the file's key wrapped by the machine key (AES-256-GCM),
recovery key id + the file's key wrapped by the recovery key (AES-256-GCM)
then repeated: [flag][length][ciphertext][16-byte tag]   (AES-256-GCM, 1 MiB parts, last part flagged)
```

`hospitalpm backup-decrypt` is the supported way. The details are in the comment at the top of
`src/HospitalPm.Infrastructure/Operations/BackupVault.cs`.
