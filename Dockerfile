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
FROM node:24-slim AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ .
RUN npm run build

# Copy source and publish.
FROM build AS publish
COPY src/ src/
COPY --from=web-build /web/dist src/HospitalPm.Api/wwwroot/
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

# The data directory for keys, licence, backups — mounted as a volume.
ENV HOSPITALPM_DATA=/var/lib/hospitalpm
ENV ASPNETCORE_URLS=http://+:5000

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
