# Hospital PM on Kubernetes

For a hospital that already runs a cluster. It is **not** the normal install (that is the Windows installer, or
`docker compose` on a Linux server), and nothing in the product needs it.

Two workloads, as everywhere else: the database and the application.

| File | What it makes |
|---|---|
| `00-namespace.yaml` | The `hospitalpm` namespace |
| `10-postgres.yaml` | PostgreSQL 18 (`aryant050/hospitalpm:DB`) as a StatefulSet with a 5 Gi volume |
| `20-app.yaml` | The application (`aryant050/hospitalpm:v0.0.6`, API and web screens on port 5000), its 5 Gi volume, and a `LoadBalancer` service |

## Deploy

The database password is **not in these files**. Choose it when you deploy, so no two installs share one, and keep
it: it is how you reach the database yourself. Use letters, numbers, `-`, `_` and `.` (a `;` or a quote breaks the
program's connection string). It is set once, when the database is first created; changing the Secret later does
not change the password inside an existing database.

```bash
kubectl apply -f 00-namespace.yaml
kubectl -n hospitalpm create secret generic hospitalpm-db --from-literal=password='YOUR-PASSWORD'
kubectl apply -f 10-postgres.yaml -f 20-app.yaml
```

To reach the database yourself (pgAdmin, DBeaver, psql), from your own machine:

```bash
kubectl -n hospitalpm port-forward pod/hospitalpm-db-0 5433:5432
# then connect to localhost:5433, database hospitalpm, user hospitalpm, and your password
```

Set the hospital's name (printed on every certificate and report) before the first start:

```bash
kubectl -n hospitalpm create configmap hospitalpm-config --from-literal=HOSPITAL_NAME="Sunrise Multispecialty Hospital" \
  --dry-run=client -o yaml | kubectl apply -f -
```

Then open the application's address (port 5000). The first visit asks for the administrator account.

```bash
kubectl -n hospitalpm get svc hospitalpm        # the address, if the cluster gives one
kubectl -n hospitalpm port-forward svc/hospitalpm 5000:5000   # or this, from your own machine
```

## Things to know

- **One application replica, and it must stay one.** The program keeps its licence, its signing keys and its
  backups on its volume and runs its own scheduled jobs; two copies would run them twice. The strategy is
  `Recreate` so an upgrade never has two pods on the volume.
- **Back up the volumes.** The two-hourly backups are written to the application's volume, which is the
  same storage as the data they protect. Copy them elsewhere (the Developer's Google Drive upload does this).
- **Plain HTTP.** Put an Ingress with TLS in front of port 5000 if the network is not trusted.
- **`hospitalpm.local` does not work here.** That name is announced with multicast, which a pod network does
  not carry. Use the service's address or a DNS name.
- **Upgrade:** change the image tag in `20-app.yaml` and apply it. The program migrates its own database on start.
- **Remove it all:** `kubectl delete namespace hospitalpm` (this deletes the data volumes too).
