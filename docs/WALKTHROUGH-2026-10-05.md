# Walkthrough of the whole software, 5 October 2026

The 1,237 automated tests all passed before this started. The point was to find what they do not look
at: real screens, many people acting in the same instant, and input nobody writes a test for.

How it was done: the full test suite, the web type-check and lint, then a throwaway copy of the app on
a throwaway database filled with the demo hospital (583 machines, 34 service requests), driven through
the browser and by scripts. Nothing touched the installed app, the development database or the real
Google Drive. The throwaway was deleted afterwards.

## Found and fixed

Each has a test in `tests/HospitalPm.IntegrationTests/WalkthroughFixTests.cs`, which fails without the fix.

| # | What was wrong | What a person saw | Fix |
|---|---|---|---|
| 1 | **Stock was lost when two people used the same part at once.** The count was read, reduced and written back, so two people both read 5 and both wrote 4. In a test, 8 simultaneous uses of a part with 5 on the shelf all succeeded, recorded 8 uses, and the shelf dropped by one. | The shelf count was wrong and could not be reconciled with the work orders. | The database now takes the part off in one statement that cannot go below zero, in the same transaction as the usage row. Undoing a use puts it back the same way. |
| 2 | **Anything the database refused was a server error with no message.** A 500-character asset tag, a NUL character in a note, a part cost of 1e20, a 300-character username. Worst, two people saving the same asset tag or completing the same PM at once: the loser got a 500. | "Something went wrong" with nothing to act on. | One middleware (`DatabaseErrorMiddleware`) turns "too long", "unusable character", "number too large" into a 400 and a duplicate into a 409, each with a plain sentence. The Locations form had its own translator that fell through to a 500, and now uses the same messages. |
| 3 | **Choosing a file that is not an Excel workbook was a server error on all six import steps** (a CSV renamed `.xlsx`, a damaged download). The code meant to catch it but caught the wrong exception types. | A server error on the import screen, the place a wrong file is most likely. | All the ways a file can be unreadable now give "The file could not be read as an Excel workbook". |
| 4 | **A fault could be reported on a condemned machine.** | A retired machine sat in "machines down" and "open requests" for ever, with nobody able to repair it. | Refused with a message. |
| 5 | **Creating or resetting a user with no password was a server error.** | A server error. | "Choose a password." |
| 6 | **On a filtered list, clicking the top bar's link did not clear the filter or search.** The page held its own copy and wrote it back into the address. On Equipment, Incidents, Gate passes, Training, Service requests, Service history and Audit. | You could not get back to the full list by clicking its name. | An outside change of the address now updates the filters. A shared `useSearchBox` hook does it for the search box. Tried in the browser on Equipment, Incidents, Gate passes, Training and Service requests; Service history and Audit use the same hook and were not clicked. |
| 7 | **On a phone, Sign out was 70 pixels off the screen**, and several pages scrolled sideways. A drop-down with a long option, a long name in a history note, a file list on the Export page, or the page header's buttons each made the whole page wider than the screen (one page was 39,000 pixels wide). | Sideways scrolling and unreachable buttons below about 470 px wide. | The top bar wraps to two rows on a phone, and grids may shrink below their widest child. All 26 pages now fit at 390 px; tablet and desktop are unchanged. |
| 8 | The demo-data tool still used role names that no longer exist, so it created no staff and then printed six logins that did not work. | Only matters when preparing a demo. | Uses the current roles and says when an account could not be made. |

## Checked and fine

- Sign-in, lockout after repeated wrong passwords, expired and tampered tokens, role limits on creating,
  deactivating and resetting accounts (the Head and the IT team cannot touch a Developer).
- Service requests end to end: the allowed status moves, resolve, close, assign, notes with the author's
  name, parts with stock checks, photos (content is checked, not the file name; 10 MB and 10-photo limits).
- PM: the form, the list of problems when it is incomplete, recording, the certificate.
- Every PDF (certificate, service report, incident and summary, gate pass, training, PM compliance, labels)
  and the QR and ZPL labels.
- Backups: Back up now, download link (one use), restore-from-file upload rejecting garbage, empty and
  damaged files. Restore itself is "installed system only" by design and is still to be tried on a clean
  install.
- Licence: issue (all the validation), install, the machine cap, lock, the lock surviving the deletion of
  one of its two files, replay of an old code refused, unlock.
- Every list and report with 23 odd query strings each (a NUL, 5,000 characters, quotes, negative and
  huge page numbers): no server errors.
- All 28 pages load with no console errors. The dashboard tiles open the list they count.

## Seen, then decided with you

| Seen | Decision |
|---|---|
| Two people changing the same work order at the same instant: the last save won. | **Fixed, with no migration.** The work order's row is held while it is changed (`SELECT ... FOR UPDATE`), so the second person waits, reads what the first left, and gets the usual "already resolved" message. Covers status, resolve, assign, breakdown type and parts. Tests: six resolves at once give one success and five conflicts, and a cancel against a resolve leaves one outcome. |
| No upper limit on free text. | **Left as it is**, by your choice. The layout copes with long text. |
| Machine dates were not cross-checked. | **Fixed.** Refused: bought in the future, installed before it was bought, a warranty that ended before the purchase. Blank dates are fine. Applies to the form, an edit and the Excel import (which names the column). An old record whose dates were already wrong can still have its notes edited; the check returns when its dates are touched. |
| An asset tag containing `/` could not be looked up by address or in a QR link. | **Fixed.** A new tag with `/` is refused with "use - instead". An old tag that already has one is left alone, so that record can still be edited. |
| The "Modules" box on the Issue licences page. | **Removed** from the page. Licences are issued with no modules; the licence file format is unchanged. |

Still seen and not changed: duplicate serial numbers are accepted (probably right: two makers can share
one), and the browser tab says "Sign in" for a moment while a reload restores the session.

## While doing this

Testing the licence I overwrote a development licence file, `src/HospitalPm.Api/bin/Debug/.../hospitalpm.licence`
(build output, not in git; the installed app is not affected). It held a licence for "QAZ" valid until
28/12/2026; a new "QAZ" licence to the same date, with no machine cap, has been put there. Nothing else
outside the throwaway was changed.
