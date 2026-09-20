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
