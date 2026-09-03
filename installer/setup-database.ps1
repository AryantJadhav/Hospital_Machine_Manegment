<#
.SYNOPSIS
    Creates and registers the bundled PostgreSQL instance.

.DESCRIPTION
    Called by the installer. Kept as a script rather than Inno Pascal because
    it is the most failure-prone part of the install and this way it can be
    run and debugged on its own.

    Idempotent by design. A hospital reinstalls after a support call, and a
    second run must not destroy the cluster holding their equipment register:
    if the data directory already has a cluster in it, that cluster is kept
    and only the service registration and the role/database checks are
    re-applied.

    Writes nothing to stdout that would leak a password. The generated
    credentials go to -OutFile, which the caller places inside the
    already-locked data directory.

.EXAMPLE
    setup-database.ps1 -PgRoot 'C:\Program Files\Hospital PM\pgsql' `
                       -DataDir 'C:\ProgramData\Hospital PM\pgdata' `
                       -Port 5433 -ServiceName HospitalPM_Postgres `
                       -OutFile 'C:\ProgramData\Hospital PM\db.json'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PgRoot,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$OutFile,
    [int]$Port = 5433,
    [string]$ServiceName = "HospitalPM_Postgres",
    [string]$AppDatabase = "hospitalpm",
    [string]$AppUser = "hospitalpm",

    # Written to unconditionally. Inno discards this script's output, so
    # without a log a failure during an install leaves no record at all -
    # which is exactly the situation where someone needs one.
    [string]$LogFile
)

$ErrorActionPreference = "Stop"

if (-not $LogFile) {
    $LogFile = Join-Path (Split-Path -Parent $OutFile) "setup-database.log"
}

function Write-Log {
    param([string]$Message)
    $line = "{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Write-Host $line
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null
        Add-Content -Path $LogFile -Value $line -Encoding ascii
    }
    catch {
        # Never let logging be the thing that fails an install.
    }
}

<#
    Runs a native executable and judges it by its exit code.

    Windows PowerShell 5.1 turns a native command's stderr into ErrorRecords
    when output is redirected, and with $ErrorActionPreference = Stop that
    terminates the script even when the program exited 0. initdb writes its
    entire progress log to stderr, so the first version of this script killed
    itself on a successful cluster creation - and would have done so on every
    hospital install, where the installer redirects output to a log.
#>
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$What,
        [string]$StdIn
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        if ($PSBoundParameters.ContainsKey("StdIn")) {
            $output = $StdIn | & $Exe @Arguments 2>&1
        }
        else {
            $output = & $Exe @Arguments 2>&1
        }

        if ($LASTEXITCODE -ne 0) {
            throw "$What failed (exit $LASTEXITCODE):`n$($output -join [Environment]::NewLine)"
        }
        return $output
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

$initdb    = Join-Path $PgRoot "bin\initdb.exe"
$pgctl     = Join-Path $PgRoot "bin\pg_ctl.exe"
$psql      = Join-Path $PgRoot "bin\psql.exe"
$pgisready = Join-Path $PgRoot "bin\pg_isready.exe"

foreach ($tool in @($initdb, $pgctl, $psql, $pgisready)) {
    if (-not (Test-Path $tool)) { throw "Missing PostgreSQL tool: $tool" }
}

function New-Secret {
    # Cryptographic randomness, not Get-Random.
    #
    # RandomNumberGenerator.Create() rather than .Fill(): Fill is .NET Core
    # only, and Windows PowerShell 5.1 - which is what a hospital PC has, and
    # what the installer invokes - runs on .NET Framework, where it does not
    # exist. Create() works on both.
    #
    # Alphanumeric output, so the value can never need escaping in a
    # connection string, a .pgpass line or a command line.
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    $alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
    -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

Write-Log "=== Database setup started (port $Port) ==="

trap {
    Write-Log "SETUP FAILED: $($_.Exception.Message)"
    break
}

# --- 1. The cluster --------------------------------------------------------
$freshCluster = -not (Test-Path (Join-Path $DataDir "PG_VERSION"))

if ($freshCluster) {
    Write-Log "Creating the database cluster..."
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

    # PostgreSQL refuses to run with administrator rights on Windows: initdb
    # and postgres re-launch themselves under a restricted token with the
    # Administrators group stripped out. The parent folder is locked to SYSTEM
    # and Administrators only, so that restricted child loses all access and
    # initdb fails with "could not create directory ... Permission denied" -
    # or, if the directory already exists, the more confusing "File exists",
    # because it could not read the directory to discover it was there.
    #
    # Granting the installing account explicitly is what makes the restricted
    # token work. It weakens nothing: that account is already an administrator
    # and could take ownership of this folder at any time.
    $me = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
    Invoke-Native -Exe "$env:SystemRoot\System32\icacls.exe" -What "granting access to the data directory" -Arguments @(
        $DataDir, "/grant", "*${me}:(OI)(CI)F", "/C") | Out-Null

    $superPassword = New-Secret
    $pwFile = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString("N"))
    try {
        # --pwfile, never an argument: command lines are visible to every user
        # on the machine in the process list.
        Set-Content -Path $pwFile -Value $superPassword -NoNewline -Encoding ascii

        # UTF8 with the C locale. Deterministic and independent of whatever
        # regional settings the hospital PC happens to carry, which matters
        # because collation affects index ordering and a cluster's locale
        # cannot be changed afterwards without recreating it.
        Invoke-Native -Exe $initdb -What "initdb" -Arguments @(
            "--pgdata=$DataDir", "--username=postgres", "--pwfile=$pwFile",
            "--encoding=UTF8", "--locale=C",
            "--auth-host=scram-sha-256", "--auth-local=scram-sha-256") | Out-Null
    }
    finally {
        if (Test-Path $pwFile) { Remove-Item $pwFile -Force }
    }
}
else {
    Write-Log "Existing cluster found - keeping it."
}

# --- 2. Listen only on loopback, on our own port ---------------------------
# The application talks to PostgreSQL from the same machine. Nothing outside
# needs to reach the database directly, and a hospital LAN is not a place to
# expose one.
$conf = Join-Path $DataDir "postgresql.conf"
$settings = @(
    "listen_addresses = 'localhost'",
    "port = $Port",
    "# Hospital PM: a ward PC is not a database server.",
    "shared_buffers = 128MB",
    "max_connections = 50"
)
$existing = Get-Content $conf | Where-Object {
    $_ -notmatch '^\s*(listen_addresses|port|shared_buffers|max_connections)\s*='
}
Set-Content -Path $conf -Value ($existing + $settings) -Encoding ascii

# --- 3. The service --------------------------------------------------------
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Log "Registering the database service..."
    Invoke-Native -Exe $pgctl -What "pg_ctl register" -Arguments @(
        "register", "-N", $ServiceName, "-D", $DataDir, "-S", "auto") | Out-Null

    # Restart on failure, like the application service. Nobody on a ward is
    # watching services.msc.
    Invoke-Native -Exe "$env:SystemRoot\System32\sc.exe" -What "sc failure" -Arguments @(
        "failure", $ServiceName, "reset=", "86400",
        "actions=", "restart/60000/restart/60000/restart/120000") | Out-Null
}

Start-Service -Name $ServiceName
Write-Log "Waiting for PostgreSQL to accept connections..."

$ready = $false
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    foreach ($attempt in 1..60) {
        & $pgisready --host=localhost --port=$Port --quiet 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
}
finally {
    $ErrorActionPreference = $previousPreference
}
if (-not $ready) {
    throw "PostgreSQL did not start within 60 seconds. See $DataDir\log for details."
}

# --- 4. The application's role and database --------------------------------
# On a fresh cluster we know the superuser password; on an existing one we do
# not, so the previously written credentials are reused.
if ($freshCluster) {
    $appPassword = New-Secret

    $passFile = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString("N"))
    try {
        # PGPASSFILE rather than PGPASSWORD: the path is not a secret, the
        # environment of a child process can be read, and a command line
        # certainly can.
        Set-Content -Path $passFile -Value "localhost:$Port`:*:postgres:$superPassword" -Encoding ascii
        $env:PGPASSFILE = $passFile

        $sql = @"
DO `$`$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$AppUser') THEN
        CREATE ROLE $AppUser LOGIN PASSWORD '$appPassword';
    ELSE
        ALTER ROLE $AppUser LOGIN PASSWORD '$appPassword';
    END IF;
END
`$`$;
"@
        Invoke-Native -Exe $psql -What "creating the application role" -StdIn $sql -Arguments @(
            "--host=localhost", "--port=$Port", "--username=postgres", "--dbname=postgres",
            "--no-password", "--quiet", "--set=ON_ERROR_STOP=1", "-f", "-") | Out-Null

        # Not in the DO block: CREATE DATABASE cannot run inside a transaction.
        $exists = Invoke-Native -Exe $psql -What "checking for the database" -Arguments @(
            "--host=localhost", "--port=$Port", "--username=postgres", "--dbname=postgres",
            "--no-password", "--quiet", "--tuples-only", "--no-align",
            "--command=SELECT 1 FROM pg_database WHERE datname = '$AppDatabase'")

        if (($exists -join "").Trim() -ne "1") {
            Invoke-Native -Exe $psql -What "creating the database" -Arguments @(
                "--host=localhost", "--port=$Port", "--username=postgres", "--dbname=postgres",
                "--no-password", "--quiet", "--set=ON_ERROR_STOP=1",
                "--command=CREATE DATABASE $AppDatabase OWNER $AppUser") | Out-Null
        }
    }
    finally {
        Remove-Item Env:\PGPASSFILE -ErrorAction SilentlyContinue
        if (Test-Path $passFile) { Remove-Item $passFile -Force }
    }

    # The caller writes this into the locked data directory. It is the only
    # place either password is recorded.
    #
    # ASCII, not utf8: PowerShell 5.1 always writes a BOM for -Encoding utf8,
    # and a leading EF BB BF makes this unparseable to anything that reads it
    # as plain JSON. The values are generated alphanumerics, so ASCII loses
    # nothing.
    @{
        host        = "localhost"
        port        = $Port
        database    = $AppDatabase
        username    = $AppUser
        password    = $appPassword
        superuser   = "postgres"
        superpass   = $superPassword
    } | ConvertTo-Json | Set-Content -Path $OutFile -Encoding ascii

    Write-Log "Database created."
}
else {
    if (-not (Test-Path $OutFile)) {
        throw "An existing cluster was found but $OutFile is missing, so the application's database password is unknown. Restore that file, or remove $DataDir to start over - which discards the existing database."
    }
    Write-Log "Reusing the existing database credentials."
}

Write-Log "PostgreSQL is ready on port $Port."
