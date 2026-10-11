# Running Hospital PM with Docker

For a hospital that has a server (or a spare PC) and wants to run Hospital PM from Docker instead of the Windows
installer. You will run two containers, the **database** and the **application**, with one command. People then
open the application in a web browser.

Hospital PM holds no patient information: equipment, work orders, engineers and contracts only.

| Image | What it is |
|---|---|
| `aryant050/hospitalpm:DB` | The database (PostgreSQL 18, with the `hospitalpm` database and user ready) |
| `aryant050/hospitalpm:v0.0.9` | The application: the web screens and the API, on port 5000 |

## What you need

- A computer or server that stays on. Linux is simplest; Windows with Docker Desktop also works.
- **Docker** installed and running. On Linux install "Docker Engine" and the "compose plugin"; on Windows or Mac install
  Docker Desktop. Check with `docker --version` and `docker compose version`.
- The file `docker-compose.hub.yml` from this project.
- Internet, **only the first time**, to download the two images (under 1 GB together). After that it runs
  without internet.
- The server must be an ordinary Intel/AMD (x64) machine. The images are not built for ARM.

## Set it up

**1. Make a folder** for it, for example `hospitalpm`, and put the compose file in it. Rename the file to
`docker-compose.yml`, so the commands below stay short.

**2. Make a file called `.env`** in the same folder, with these lines:

```
POSTGRES_PASSWORD=choose-a-password
HOSPITAL_NAME=Your Hospital Name
APP_PORT=5000
```

- **`POSTGRES_PASSWORD`** is the database password. Choose it now and write it down: it is how you reach the
  database yourself. Use letters, numbers, `-`, `_` and `.`; a `;` or a quote mark stops the application from
  connecting. It is fixed when the database is first made. Changing it here later does **not** change it inside an
  existing database.
- **`HOSPITAL_NAME`** is printed at the top of every PM certificate and service report.
- **`APP_PORT`** is the port people open. Use `5000` on a server of its own.

**3. Start it:**

```bash
docker compose up -d
```

The first start downloads the images and builds the database, which takes a minute or two.
Check both are healthy with `docker compose ps`.

**4. Open it** in a browser: `http://THE-SERVER:5000` (the server's own address, for example `http://192.168.1.10:5000`).
The first visit asks you to create the **administrator account**. Do that, then sign in.

## Let other computers use it

- Use `APP_PORT=5000` (or any free port) and **allow that port** in the server's firewall.
  Ubuntu: `sudo ufw allow 5000/tcp`. Windows will ask the first time; allow it on the network the PCs are on.
- Give the server a **fixed address** on your network (ask whoever runs the router), so the address people type
  does not change. The name `hospitalpm.local` does **not** work with Docker.
- The connection is plain HTTP, normal inside a hospital network. If the network is not trusted, put a web
  server with a certificate in front of it.
- If a phone or PC cannot connect but the server can, the usual cause is a router setting that stops devices on
  the Wi-Fi from reaching each other ("client isolation" or a guest network).

## Looking at the database yourself

Use pgAdmin, DBeaver or `psql` on the same machine:

| | |
|---|---|
| Host | `localhost` |
| Port | `5435` (change it with `DB_PORT` in `.env`) |
| Database | `hospitalpm` |
| User | `hospitalpm` |
| Password | the one in your `.env` |

By default the database port is open **to that machine only**. To use it from another PC, add `DB_BIND=0.0.0.0`
to `.env`, run `docker compose up -d` again, and allow the port in the firewall for that one PC's address only.
Anyone who can reach the port can try to sign in to the database.

## Everyday running

| To | Run (in the folder) |
|---|---|
| See that it is running | `docker compose ps` |
| See what it is doing | `docker compose logs -f backend` |
| Stop it | `docker compose stop` |
| Start it again | `docker compose start` |
| Restart it | `docker compose restart` |

Both containers are set to start again by themselves if they stop, and when the machine restarts, as long as
Docker itself starts with the machine. On Windows, set Docker Desktop to start when you sign in.

**Your data lives in two Docker volumes**, `hospitalpm-pgdata` (the database) and `hospitalpm-appdata` (the
licence, the signing keys and the backups). Stopping or removing the containers does **not** delete them.
Deleting the volumes deletes the hospital's records. Only one copy of Hospital PM can use these names on a machine.

## The licence

The software works without a licence, but says it is unlicensed. Your supplier gives you a licence file; the IT team
(or the Developer) pastes it into the **Licence** page (Admin menu). A licence can limit the number of
machines on the register and can be locked or unlocked by your supplier with a code.

## Backups

- A backup of the database is made **automatically every two hours**, and the last 168 (two weeks) are kept.
- They are written to the `hospitalpm-appdata` volume, which is on **the same machine** as the data they protect. A
  backup that stays on a failed disk is no use, so **copy them to another machine regularly.**
- The Backups page (Developer only) has a **Back up now** button. It is hidden unless you add
  `FEATURE_BACKUPS=true` to `.env`.
- To copy the backups to the current folder:

```bash
docker compose cp backend:/var/lib/hospitalpm/backups ./backups-copy
```

Each file is named like `hospitalpm-20261011-211146-IST.dump`. The Developer can also switch on an automatic copy to
Google Drive. That is set up by the Developer, not in this guide.

## Getting the data back from a backup

Use this after a failed disk, or when moving to a new server. It replaces the database with the backup, so
**everything entered since that backup is lost.** The Restore button inside the application works only on the
Windows installer; on Docker do this.

First set the system up as above on the new machine (steps 1 to 3), and put the backup file in the folder. Then, in
that folder (on Windows use PowerShell or Command Prompt), one command at a time. Use your own file name:

```bash
docker compose stop backend
```
```bash
docker compose cp hospitalpm-20261011-211146-IST.dump db:/tmp/restore.dump
```
```bash
docker compose exec db psql -U hospitalpm -d postgres -c "DROP DATABASE hospitalpm WITH (FORCE)" -c "CREATE DATABASE hospitalpm OWNER hospitalpm"
```
```bash
docker compose exec db pg_restore -U hospitalpm -d hospitalpm --no-owner --no-privileges --exit-on-error /tmp/restore.dump
```
```bash
docker compose exec db rm /tmp/restore.dump
```
```bash
docker compose start backend
```

Wait a minute, open the application, and sign in with an account that existed when the backup was made. The
`pg_restore` step should print nothing; if it prints an error, stop and keep the backup file.

## Updating to a newer version

1. Copy the backups out, as above.
2. In `.env`, set `HOSPITALPM_TAG` to the new version, for example `HOSPITALPM_TAG=v0.0.10`.
3. Run `docker compose pull`, then `docker compose up -d`.

The application brings its own database up to date when it starts. Your data volumes are kept.

## If something is wrong

| What you see | What to do |
|---|---|
| `docker compose up` stops and says `POSTGRES_PASSWORD` must be set | The `.env` file is missing, or is not in the same folder |
| The application keeps restarting, and `docker compose logs backend` says `password authentication failed` | The password in `.env` is not the one the database was first made with. Put back the original, or remove the data volume (this deletes the data) to start fresh |
| `port is already allocated` | Something else uses that port. Change `APP_PORT` (or `DB_PORT`) in `.env` and run `docker compose up -d` |
| Works on the server, not on other PCs | Firewall on the server, or the router blocks devices from reaching each other (see above) |
| The page loads but sign-in fails after a restore | Use an account that existed when the backup was made |

## Taking it away

```bash
docker compose down
```

removes the containers and keeps the data. To delete the data as well, add `-v` (`docker compose down -v`). That
cannot be undone, so copy the backups out first.

## Kubernetes

For a hospital that already runs a Kubernetes cluster, the same two images are described in
`deploy/kubernetes/`. See the README there. It is not needed for an ordinary install.
