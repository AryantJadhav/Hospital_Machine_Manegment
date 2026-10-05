# Issuing a licence key

A licence is a signed block of text. You make one per hospital, send it by email
or WhatsApp, and their Administrator pastes it into **Admin ▸ Licence**. The
software checks the signature offline against the public key built into the
release. Only the private key, which stays on your machine, can make one.

The signing key lives in `D:\Coding\_hospitalpm-signing-key\signing-key.pem`,
outside the repository. Never commit it, never send it, never generate a second
one: a new key would make every licence already issued invalid.

## Making a key

**A pilot: runs for a length of time from the day it is installed**

```
dotnet run --project tools/HospitalPm.LicenceTool -- sign ^
  --key "D:\Coding\_hospitalpm-signing-key\signing-key.pem" ^
  --hospital "Sahyadri Hospital, Pune" ^
  --days 84 ^
  --notes "pilot" ^
  --out sahyadri-pilot.licence
```

`--days 84` (12 weeks) starts counting when the hospital first installs the key,
so you can issue it before you know the install date. Pasting the same key in
again later does not restart the clock; a new key does.

**A paid install: ends on a calendar date**

```
... --expires 2027-09-30 --out sahyadri.licence
```

Use `--days` or `--expires`, not both. Leave both out for a perpetual licence.
Optional: `--max-equipment 500`, `--modules a,b`, `--notes "PO 2026/114"`.

Check a file the way the product will: `... verify --public-key <file or key> --file x.licence`.

## What happens when it ends

| When | What the hospital sees |
|---|---|
| Up to 14 days before the end | Administrators see a banner with the days left. |
| Up to the end date | Nothing. |
| End date + 1 to 14 days (grace) | Everyone sees a warning banner. Everything still works. |
| From day 15 | **Read-only.** Everything can be opened and printed. Nothing new can be recorded. Everyone sees a red banner saying why. |

In read-only, signing in, taking and restoring backups, installing updates and
installing a renewal key all still work. Installing a renewal lifts it at once,
with no restart. The nightly PM generation keeps running, so nothing is missing
when a renewal arrives.

An install with no licence file runs normally and says "unlicensed". That is
deliberate: a pilot install has to work before a key exists.

## Limits, said plainly

- A key is not tied to a machine. The same file would work on a second install.
  The hospital's name is on every report it prints, which discourages that.
- The software remembers the latest date it has seen, so putting the clock back
  does not revive an expired licence. Someone with administrator rights on the
  machine can still delete the small `hospitalpm.licence.state` file beside the
  licence and reset that. This stops casual cases, not a determined one.
- If a machine's clock was wrongly set far into the future and then corrected,
  the remembered date will keep the licence looking expired. Deleting
  `hospitalpm.licence.state` fixes it.

## Issuing from the program, and locking an installation

The same licences can be made from **Admin ▸ Issue licences**, on your own copy of the software. That page appears
only for the Developer account, and only works where `Licence:SigningKeyPath` points at the signing key. A hospital's
installation has no such key, so it shows the page nowhere, and cannot issue anything.

Set it on your own machine, in `appsettings.Development.json` or the environment:

```
Licence__SigningKeyPath = D:\Coding\_hospitalpm-signing-key\signing-key.pem
Licence__PublicKey      = <the contents of public-key.txt>
```

The program refuses to sign if that key is not the one whose public half it carries, because what it signed would not
verify at a hospital. Every licence made is kept in a list, with the exact signed text, so it can be sent again.

### Lock and unlock

On a licence in that list, **Lock…** makes a short signed *code*, and **Unlock** makes another. You send the code by
message. At the hospital it is pasted into **Admin ▸ Licence ▸ A code from your supplier**. Nothing is sent to the
installation: there is no connection to one, and the code is checked offline against the public key built into the
program, like a licence.

A **locked** installation is locked completely. Nobody can sign in, a person already signed in is turned away on
their next click, and no record opens. What stays up is a lock screen that shows the hospital's name and the licence
id, so they can quote it on the phone, and a box for an unlock code. The nightly backup still runs.

This is different from expiry, which only ever makes the software read-only.

**Each licence's codes are numbered.** A code is accepted only if its number is higher than the last one the
installation accepted, so an old unlock cannot undo a newer lock, an old lock cannot undo a newer unlock, and a code
entered twice does nothing the second time. An unlock names the licence that did the locking, so replacing the licence
file with another does not slip past it.

The lock is remembered in two places, one beside the licence and one in the data folder's `keys` folder. Deleting one
puts it back from the other, and restoring an old backup cannot unlock anything, because neither file is in the
database.

### What it cannot do, said plainly

- **It only works if the hospital enters the code.** There is no phone-home. A hospital that never enters a lock code is
  never locked. A lock is something to send when you have been asked to, or have agreed it, not a way to reach into a
  machine.
- **An installation with no licence file cannot be locked,** because a code names a licence id.
- Someone with administrator rights on the machine can delete both lock files. This stops casual cases, not a
  determined one.
- The Developer account on the **same** installation is locked too. Make your codes on your own copy, never on the
  hospital's.

## The most machines a licence allows

`--max-equipment` (or the box on the page) is enforced. Adding a machine that would go over it is refused with the
count and the limit, and an Excel import is judged as a whole: if all of it would not fit, none of it goes in, and
the validation says so before anything is written. Reading, editing and removing what is already there is not
touched. A licence with no limit has none.
