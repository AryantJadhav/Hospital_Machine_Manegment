# Backups on Google Drive

Each night, after the backup is written, the encrypted file is copied to Google Drive with **rclone**. It is off unless
switched on, it never fails a backup, and a hospital with no internet is not affected.

Only the **encrypted** files are ever sent (a plain dump is refused by the code, whatever it is called). What is on the
drive is useless without the keys: the machine key never leaves the machine, and the recovery key is written down
elsewhere.

Only the Developer sees the Drive card on the Backups page and can press **Upload now**.

## How it works

1. A backup succeeds. The nightly job (not "Back up now") asks the uploader to send what has not gone yet.
2. It sends the newest files first with `rclone copyto --checksum`, one at a time, into a folder named after the
   licence's id, under the shared root folder. It stops at the first failure and records the reason.
3. Then it lists that folder and removes the oldest beyond the number to keep (`KeepCount`, 14). It only ever touches
   files this program named (`hospitalpm-*.dump.enc`).
4. What has gone is remembered in `.drive-state.json` beside the backups. Losing it means a file is sent again, which
   is harmless.

rclone runs once and exits. It is not a service and is not left running. The Google key is given to it in its
**environment** for that one command: never on a command line, and never written to a configuration file.

No licence, no upload: the folder is named after the licence, and a locked installation sends nothing.

## Settings

All under `Backup:Drive` in `appsettings.json`, or as environment variables (`Backup__Drive__Enabled=true`).

| Setting | Default | |
|---|---|---|
| `Enabled` | `false` | Switch it on |
| `RclonePath` | beside the program, then the PATH | Where rclone is |
| `RcloneConfigFile` + `RemoteName` | | Use a remote from an existing rclone.conf (e.g. `gdrive`) instead of building one |
| `TokenJson` / `TokenFile` | | A personal Google account's sign-in token (the JSON from `rclone config show`), as text or a file. Used with the narrow `drive.file` scope. Takes precedence over a service account |
| `ServiceAccountJson` / `ServiceAccountFile` | | A Workspace service account's key, as the JSON file's text or a file |
| `RootFolderId` | | The Drive folder shared with the service account |
| `Folder` | the licence id | This installation's folder under the root |
| `KeepCount` | `14` | How many backups to keep on the drive |
| `TimeoutMinutes` | `120` | How long one upload may run |
| `Remote` | | Extra rclone settings, e.g. `Remote:type=local` and `Folder=D:/offsite` to copy to a plain folder or share instead |

## Setting up with a personal Google account (what you chose)

A service account cannot be used with a personal Gmail: Google gives service accounts no storage in a personal Drive, so
uploads fail with a quota error. A personal account signs in as itself with a token that rclone makes once. Nothing here
costs anything, and no Google Cloud project is needed: rclone's own built-in sign-in is used (the same way as any
`rclone config` for Drive).

**The quickest way, on a machine where rclone is already signed in to Drive** (for example `gdrive`):

```
Backup__Drive__Enabled         = true
Backup__Drive__RcloneConfigFile = C:\Users\<you>\AppData\Roaming\rclone\rclone.conf
Backup__Drive__RemoteName      = gdrive
```

The program uses that remote, and rclone keeps its own sign-in renewed in that file. Use this on your own PC. A remote made
with the default `drive` scope can see **all** of your Drive, so do not copy its token into a program that is handed to
hospitals.

**For anything that leaves your machine, make a narrow sign-in:**

1. Run `rclone config create hp drive scope=drive.file` (no client id, no secret). A browser opens: sign in with the
   personal account, and accept the "unverified app" warning once. With `drive.file` the program can see only the files
   it created itself, never the rest of your Drive, so a leaked token cannot read your photos or documents.
2. Run `rclone config show hp` and copy only the `{...}` after `token = `.
3. Save it as `drive-token.json` somewhere private, and set `Backup:Drive:TokenFile` to it (or the text in
   `Backup:Drive:TokenJson`). The program sets the `drive.file` scope itself.
4. Set `Backup:Drive:Enabled` to `true`, and restart.

If you ever see *"Access blocked: Authorisation error / The OAuth client was not found" (401 invalid_client)* while signing
in, the remote has a **client id of its own that is wrong** (a typo or a half-pasted value). Delete that remote and make it
again without one. A real Google client id is about 70 characters long and ends in `.apps.googleusercontent.com`.

Things to know about a personal account:

- The token has a long-lived refresh token. Google can end it if the account's password changes, if the token is
  unused for six months, or if you remove the app's access in your Google account's security page. The card then shows
  the reason, and the fix is to make a new token (steps 1 and 2).
- It is `drive.file`, so it cannot see anything the program did not create. It can still **delete what the program
  made**, so a leaked token can wipe the backups of every hospital, though not read them.
- Storage is the account's: 15 GB free, shared with your Gmail and Photos.

## Setting up Google with a Workspace service account (the other way)

1. A Google **Workspace** account of its own, with 2-step sign-in.
2. In Google Cloud, a project used for nothing else. Turn on the **Google Drive API**.
3. Create a **service account** in it and make a JSON key.
4. In Drive, make a folder, and **share it with the service account's email address** as an editor. Its id (the last part
   of the folder's address) is `RootFolderId`.
5. Give the key to the build as a secret. Do not put it in the repository, in chat, or in an image on Docker Hub.

## The risk this carries, said plainly

You chose to use one Google account for every hospital, with the key built into the program. That means:

- **Anyone who gets a copy of the program can take the key out of it,** and then read, overwrite or delete every
  hospital's backups on that account. Treat the key as one that will leak, and rotate it by a new release.
- **Never put the key in a public Docker image.** `docker inspect` shows an image's environment to anyone. The public
  images on Docker Hub must be built without it; the key belongs in an installer or a private image only.
- **What the files hold is encrypted with each hospital's own keys,** so a leak lets someone delete or fill the account,
  not read the records. Each hospital has its own folder, named by its licence id.
- **All hospitals share one account's storage** (15 GB on a free account, more on Workspace).
- **We would be holding hospital data,** encrypted. The hospital should be told, and agree.

## What is tested, and what is not

Tested: the sending logic against a fake rclone, and the real rclone against a folder on disk (the same commands and the
same environment settings, so the copy, the checksum, the trimming and the no-resend are proven). **Not tested: Google
itself.** That needs the account and key above.

## Shipping rclone

rclone is not part of the program. The Windows installer should place `rclone.exe` beside it (an optional component,
about 25 MB), or an administrator can set `RclonePath`. In the backend Docker image it is installed with the image's
package manager. Windows antivirus sometimes flags `rclone.exe`: ship the official release and publish its checksum.
