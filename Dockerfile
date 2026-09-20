# Hospital PM — Linux container image
#
# The application is published as a self-contained, single-file binary. The
# container needs only the native runtime dependencies (libssl, zlib, etc.),
# not the .NET SDK or runtime — exactly the same constraint as a bare-metal
# Linux install.
#
# Build:
#   1. Publish the binary:  dotnet publish src/HospitalPm.Api -c Release -r linux-x64
#   2. Build the web UI:    cd web && npm ci && npm run build
#   3. Build the image:     docker build -t hospitalpm .
#
# Or use docker compose (see docker-compose.yml), which does all of this.

# --- Stage 1: build --------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first — this layer is cached unless a .csproj or props file changes.
COPY Directory.Build.props .
COPY global.json .
COPY dotnet-tools.json .
COPY HospitalPm.slnx .
COPY src/HospitalPm.Api/HospitalPm.Api.csproj src/HospitalPm.Api/
COPY src/HospitalPm.Domain/HospitalPm.Domain.csproj src/HospitalPm.Domain/
COPY src/HospitalPm.Infrastructure/HospitalPm.Infrastructure.csproj src/HospitalPm.Infrastructure/
RUN dotnet restore src/HospitalPm.Api/HospitalPm.Api.csproj -r linux-x64

# Build the web UI.
#
# Vite does not write to web/dist: vite.config.ts sends the build to
# ../src/HospitalPm.Api/wwwroot so the published binary carries the UI. The
# stage therefore keeps the repository's own layout (web/ beside src/) so that
# relative path resolves, and the next stage copies from where Vite really wrote.
FROM node:24-slim AS web-build
WORKDIR /repo/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ .
RUN npm run build && test -f /repo/src/HospitalPm.Api/wwwroot/index.html

# Copy source and publish.
FROM build AS publish
COPY src/ src/
COPY --from=web-build /repo/src/HospitalPm.Api/wwwroot src/HospitalPm.Api/wwwroot/
RUN dotnet publish src/HospitalPm.Api/HospitalPm.Api.csproj \
    -c Release \
    -r linux-x64 \
    --no-restore \
    -o /app/publish

# --- Stage 2: runtime -------------------------------------------------------
# runtime-deps is the smallest base that carries the native libraries a
# self-contained .NET app needs (libssl, libstdc++, etc.) without the managed
# runtime, which is already inside the binary.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app

# Two things the runtime-deps image does not carry, installed at build time
# only - nothing here reaches the network when the container runs:
#
#  - curl, because the health checks (here and in docker-compose.yml) call it.
#    Without it every check fails and the container reports unhealthy while
#    serving perfectly well.
#  - pg_dump, because nightly backups shell out to it. Without it a Linux
#    install has no backups at all. It must be at least as new as the server
#    (compose runs PostgreSQL 17), and Ubuntu's own client is 16, so this
#    comes from the PostgreSQL project's apt repository.
RUN apt-get update     && apt-get install -y --no-install-recommends ca-certificates curl gnupg     && install -d /usr/share/keyrings     && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc         | gpg --dearmor -o /usr/share/keyrings/pgdg.gpg     && . /etc/os-release     && echo "deb [signed-by=/usr/share/keyrings/pgdg.gpg] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main"         > /etc/apt/sources.list.d/pgdg.list     && apt-get update     && apt-get install -y --no-install-recommends postgresql-client-17     && apt-get purge -y --auto-remove gnupg     && rm -rf /var/lib/apt/lists/*

# The data directory for keys, licence, backups — mounted as a volume.
ENV HOSPITALPM_DATA=/var/lib/hospitalpm
ENV ASPNETCORE_URLS=http://+:5000

# Where the app keeps what must outlive the container. The Windows installer
# writes these paths into the settings file; a container has no such file, and the
# defaults are relative to the binary, which is inside the container. Backups
# written there were lost the first time the container was recreated - which is
# what an upgrade does - along with the licence. Found by the container smoke
# test (tools/container-smoke-test.sh), which looks for the backup on the volume.
ENV Backup__Directory=/var/lib/hospitalpm/backups
ENV Update__Directory=/var/lib/hospitalpm/updates
ENV Update__StagingDirectory=/var/lib/hospitalpm/updates/staging
ENV Licence__Path=/var/lib/hospitalpm/hospitalpm.licence

RUN mkdir -p /var/lib/hospitalpm/backups \
             /var/lib/hospitalpm/keys \
             /var/lib/hospitalpm/updates

COPY --from=publish /app/publish .

# The binary is self-contained and single-file.
RUN chmod +x hospitalpm

EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["./hospitalpm"]
