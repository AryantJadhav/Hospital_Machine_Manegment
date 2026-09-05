<#
.SYNOPSIS
    Restores the database from one of Hospital PM's backups.

.DESCRIPTION
    The other half of the backup feature. A dump nobody can restore is a file,
    not a backup.

    Runs outside the application on purpose. Restoring means dropping and
    recreating the database, which cannot be done from a process that is
    connected to it - and Hangfire's own tables live in that database, so a
    running job server would be pulled out from under itself halfway through.
    The application service is therefore stopped first and started again at
    the end.

    Before anything is dropped, the current database is dumped to
    pre-restore-<timestamp>.dump. Restoring is the one operation in this
    system that destroys data on purpose, and it must not be a one-way door:
    if someone restores the wrong file, the state they just replaced is still
    on disk.

    Every step is logged to -LogFile, because the person running this is
    having a bad day and will need to explain afterwards what happened.

.EXAMPLE
    restore-database.ps1 -PgRoot 'C:\Program Files\Hospital PM\pgsql' `
                         -CredentialsFile 'C:\ProgramData\Hospital PM\db.json' `
                         -DumpFile 'C:\ProgramData\Hospital PM\backups\hospitalpm-20260901-023000.dump' `
                         -BackupDir 'C:\ProgramData\Hospital PM\backups' `
                         -LogFile 'C:\ProgramData\Hospital PM\restore.log'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PgRoot,
    [Parameter(Mandatory)][string]$CredentialsFile,
    [Parameter(Mandatory)][string]$DumpFile,
    [Parameter(Mandatory)][string]$BackupDir,
    [Parameter(Mandatory)][string]$LogFile,
    [string]$AppServiceName = "HospitalPM"
)

$ErrorActionPreference = "Stop"

function Write-Log {
    param([string]$Message)
    $line = "{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -Encoding ascii
}

<#
    Runs a native executable and judges it by its exit code.

    Windows PowerShell 5.1 turns a native command's stderr into ErrorRecords
    when output is redirected, and with $ErrorActionPreference = Stop that
    terminates the script even when the program exited 0. pg_dump and
    pg_restore both write progress to stderr.
#>
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$What
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $Exe @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "$What failed (exit $LASTEXITCODE):`n$($output -join [Environment]::NewLine)"
        }
        return $output
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

$pgRestore = Join-Path $PgRoot "bin\pg_restore.exe"
$pgDump    = Join-Path $PgRoot "bin\pg_dump.exe"
$psql      = Join-Path $PgRoot "bin\psql.exe"

foreach ($tool in @($pgRestore, $pgDump, $psql)) {
    if (-not (Test-Path $tool)) { throw "Missing PostgreSQL tool: $tool" }
}
if (-not (Test-Path $DumpFile)) { throw "No backup file at $DumpFile" }
if (-not (Test-Path $CredentialsFile)) { throw "No database credentials at $CredentialsFile" }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null
Write-Log "=== Restore started ==="
Write-Log "Backup file: $DumpFile"

# Same reasoning as setup-database.ps1: PGPASSWORD in the environment takes
# precedence over the PGPASSFILE this script writes, so a machine that has one
# set authenticates with someone else's password no matter what is passed
# below.
#
# The rest are cleared as hygiene rather than because they override us -
# --host, --port, --username and --dbname are explicit connection parameters
# and libpq ranks those above the environment. PGSERVICE, PGOPTIONS and
# PGSSLMODE can still colour the session, and this script drops and recreates
# a database, so it is worth leaving nothing to chance.
$inheritedPg = @()
foreach ($name in @('PGPASSWORD', 'PGPASSFILE', 'PGUSER', 'PGDATABASE',
                    'PGHOST', 'PGHOSTADDR', 'PGPORT', 'PGSERVICE',
                    'PGSERVICEFILE', 'PGOPTIONS', 'PGSSLMODE', 'PGREQUIRESSL',
                    'PGCLIENTENCODING', 'PGAPPNAME', 'PGCONNECT_TIMEOUT')) {
    if (Test-Path "Env:\$name") {
        $inheritedPg += $name
        Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
    }
}

if ($inheritedPg.Count -gt 0) {
    # Names only - PGPASSWORD's value is someone else's secret.
    Write-Log "Ignoring inherited PostgreSQL environment settings: $($inheritedPg -join ', ')"
}

$creds = Get-Content $CredentialsFile -Raw | ConvertFrom-Json
$dbHost = $creds.host
$dbPort = $creds.port
$dbName = $creds.database
$appUser = $creds.username

# PGPASSFILE rather than PGPASSWORD: the path is not a secret, a command line
# certainly is, and a child process's environment can be read.
$passFile = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString("N"))
$safetyDump = $null
$dropped = $false

try {
    Set-Content -Path $passFile -Encoding ascii -Value @(
        "${dbHost}:${dbPort}:*:$($creds.superuser):$($creds.superpass)"
        "${dbHost}:${dbPort}:*:${appUser}:$($creds.password)"
    )
    $env:PGPASSFILE = $passFile

    # --- 1. Is this file actually restorable? -----------------------------
    # Checked before the application is stopped, so a corrupt or truncated
    # file costs nothing but a moment.
    Write-Log "Checking the backup file can be read..."
    Invoke-Native -Exe $pgRestore -What "reading the backup file" -Arguments @("--list", $DumpFile) | Out-Null
    Write-Log "Backup file is readable."

    # --- 2. Stop the application ------------------------------------------
    $service = Get-Service -Name $AppServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne "Stopped") {
        Write-Log "Stopping $AppServiceName..."
        Stop-Service -Name $AppServiceName -Force
        # Stop-Service returns before the process has fully exited; the
        # connections it holds would block the DROP below.
        Start-Sleep -Seconds 8
        Write-Log "Stopped."
    }

    # --- 3. Safety copy of what is about to be destroyed -------------------
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $safetyDump = Join-Path $BackupDir ("pre-restore-{0:yyyyMMdd-HHmmss}.dump" -f (Get-Date))

    Write-Log "Backing up the current database to $(Split-Path -Leaf $safetyDump)..."
    Invoke-Native -Exe $pgDump -What "the safety backup" -Arguments @(
        "--format=custom", "--no-owner", "--no-privileges",
        "--host=$dbHost", "--port=$dbPort", "--username=$($creds.superuser)",
        "--dbname=$dbName", "--file=$safetyDump") | Out-Null

    if (-not (Test-Path $safetyDump) -or (Get-Item $safetyDump).Length -eq 0) {
        throw "The safety backup wrote no data, so the restore was abandoned before anything was destroyed."
    }
    Write-Log "Safety backup written ($([math]::Round((Get-Item $safetyDump).Length / 1KB)) KB)."

    # --- 4. Disconnect everything else -------------------------------------
    # A single leftover connection makes DROP DATABASE fail.
    Write-Log "Closing other connections..."
    Invoke-Native -Exe $psql -What "closing connections" -Arguments @(
        "--host=$dbHost", "--port=$dbPort", "--username=$($creds.superuser)",
        "--dbname=postgres", "--no-password", "--quiet",
        "--command=SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$dbName' AND pid <> pg_backend_pid()") | Out-Null

    # --- 5. Replace the database -------------------------------------------
    # Dropped and recreated rather than restored with --clean: --clean leaves
    # anything the dump does not mention, so a restore would silently inherit
    # tables and triggers from the database being replaced.
    Write-Log "Recreating the database..."
    Invoke-Native -Exe $psql -What "dropping the database" -Arguments @(
        "--host=$dbHost", "--port=$dbPort", "--username=$($creds.superuser)",
        "--dbname=postgres", "--no-password", "--quiet", "--set=ON_ERROR_STOP=1",
        "--command=DROP DATABASE IF EXISTS $dbName") | Out-Null
    $dropped = $true

    Invoke-Native -Exe $psql -What "creating the database" -Arguments @(
        "--host=$dbHost", "--port=$dbPort", "--username=$($creds.superuser)",
        "--dbname=postgres", "--no-password", "--quiet", "--set=ON_ERROR_STOP=1",
        "--command=CREATE DATABASE $dbName OWNER $appUser") | Out-Null

    # --- 6. Restore ---------------------------------------------------------
    # Restored AS the application's own role, not as the superuser.
    #
    # The superuser can restore too, but then every table, trigger and
    # function belongs to it, and putting that right afterwards is not
    # possible: REASSIGN OWNED BY postgres is refused outright because system
    # objects are owned by that role too. Restoring as the database's owner
    # means the ownership is right the moment the restore finishes.
    Write-Log "Restoring..."
    Invoke-Native -Exe $pgRestore -What "the restore" -Arguments @(
        "--host=$dbHost", "--port=$dbPort", "--username=$appUser",
        "--dbname=$dbName", "--no-owner", "--no-privileges", "--exit-on-error",
        $DumpFile) | Out-Null

    # --- 7. Did anything actually arrive? ----------------------------------
    # pg_restore can exit zero having restored an empty archive.
    # Counted as the application's role, which also proves that role can
    # actually see what was restored.
    $tables = Invoke-Native -Exe $psql -What "counting the restored tables" -Arguments @(
        "--host=$dbHost", "--port=$dbPort", "--username=$appUser",
        "--dbname=$dbName", "--no-password", "--quiet", "--tuples-only", "--no-align",
        "--command=SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'")

    $tableCount = [int](($tables -join "").Trim())
    if ($tableCount -lt 1) {
        throw "The restore finished but the database has no tables."
    }
    Write-Log "Restored $tableCount tables."

    Write-Log "=== Restore finished successfully ==="
}
catch {
    Write-Log "RESTORE FAILED: $($_.Exception.Message)"

    if ($dropped -and $safetyDump) {
        # The worst moment this script has: the old database is gone and the
        # new one did not arrive. Say exactly how to get back, in the log the
        # operator is already looking at.
        Write-Log ""
        Write-Log "The database was replaced and the restore did not complete."
        Write-Log "The state from before this attempt is saved at:"
        Write-Log "    $safetyDump"
        Write-Log "Run this script again with -DumpFile pointing at that file to put it back."
    }
    elseif ($safetyDump) {
        Write-Log "Nothing was destroyed. The database is as it was."
    }

    throw
}
finally {
    Remove-Item Env:\PGPASSFILE -ErrorAction SilentlyContinue
    if (Test-Path $passFile) { Remove-Item $passFile -Force }

    # Started again whatever happened. A hospital left with a stopped service
    # and no explanation is worse off than one with an unchanged database.
    $service = Get-Service -Name $AppServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne "Running") {
        Write-Log "Starting $AppServiceName..."
        try {
            Start-Service -Name $AppServiceName
            Write-Log "Started."
        }
        catch {
            Write-Log "Could not start $AppServiceName : $($_.Exception.Message)"
        }
    }
}
