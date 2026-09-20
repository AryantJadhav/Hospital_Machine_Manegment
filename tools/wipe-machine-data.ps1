<#
.SYNOPSIS
    Deletes every machine on an installed Hospital PM, and everything recorded
    against them, after taking a backup.

.DESCRIPTION
    Vendor-side, like the demo-data tool: it is never shipped to a hospital.

    Hospital PM has no "delete machine" button on purpose. Machines are condemned,
    not deleted, because completed PM certificates and work orders must stay
    readable for years, and the database refuses to change them. Clearing out demo
    or test data before a real go-live is the one time that is wanted, and it needs
    the database's own credentials, so it is done here, on the machine, by an
    administrator.

    Removed:  machines, their PM schedules, PM tasks and completed PMs (with their
              signatures), and their work orders and notes.
              With -IncludeLocations, also every place (organisation, sites,
              buildings, floors, departments, rooms), so they can be entered afresh.
    Kept:     checklists and every version of them, staff and their roles,
              settings, the licence, and the backup history. Places are kept
              unless -IncludeLocations is given.
    Work order numbers start again at 1.

    Before anything is deleted:
      - it must be run as Administrator, and you must type DELETE;
      - the database is backed up to <data folder>\backups\pre-wipe-<time>.dump and
        the backup is read back to prove it opens.

    The protection that normally makes recorded work permanent is switched off for
    one transaction and back on before it commits. The audit trail stays on, so the
    deletions themselves are recorded in it.

    To undo it, restore that backup with the restore script in the install folder
    (the command is printed at the end).

.EXAMPLE
    # From an elevated PowerShell (Run as administrator):
    .\tools\wipe-machine-data.ps1

.EXAMPLE
    # Machines and places both:
    .\tools\wipe-machine-data.ps1 -IncludeLocations
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:ProgramData "Hospital PM"),
    [string]$PgRoot  = (Join-Path $env:ProgramFiles "Hospital PM\pgsql"),
    [string]$AppServiceName = "HospitalPM",
    [switch]$IncludeLocations
)

$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell (right-click PowerShell, Run as administrator). The database credentials are readable only by administrators."
}

$credentialsFile = Join-Path $DataDir "db.json"
$psql   = Join-Path $PgRoot "bin\psql.exe"
$pgDump = Join-Path $PgRoot "bin\pg_dump.exe"
$pgRestore = Join-Path $PgRoot "bin\pg_restore.exe"

foreach ($path in @($credentialsFile, $psql, $pgDump, $pgRestore)) {
    if (-not (Test-Path $path)) { throw "Not found: $path. Is Hospital PM installed here?" }
}

# The same reasoning as restore-database.ps1: anything PostgreSQL-related in the
# environment can quietly point these tools at a different server.
foreach ($name in @('PGPASSWORD', 'PGPASSFILE', 'PGUSER', 'PGDATABASE', 'PGHOST', 'PGHOSTADDR', 'PGPORT',
                    'PGSERVICE', 'PGSERVICEFILE', 'PGOPTIONS', 'PGSSLMODE', 'PGCLIENTENCODING')) {
    Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
}

$creds = Get-Content $credentialsFile -Raw | ConvertFrom-Json

$passFile = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString("N"))

$wipeSql = @'
\set ON_ERROR_STOP on
BEGIN;

ALTER TABLE work_order_note DISABLE TRIGGER trg_work_order_note_append_only;
ALTER TABLE pm_completion   DISABLE TRIGGER trg_pm_completion_immutable;
ALTER TABLE pm_task         DISABLE TRIGGER trg_pm_task_no_delete_completed;
ALTER TABLE diagnosis       DISABLE TRIGGER trg_diagnosis_immutable;

DELETE FROM work_order_note;
DELETE FROM work_order;
DELETE FROM pm_completion;
DELETE FROM pm_task;
DELETE FROM pm_schedule;
DELETE FROM diagnosis;
DELETE FROM equipment_move;
DELETE FROM equipment;
--LOCATIONS--
ALTER TABLE work_order_note ENABLE TRIGGER trg_work_order_note_append_only;
ALTER TABLE pm_completion   ENABLE TRIGGER trg_pm_completion_immutable;
ALTER TABLE pm_task         ENABLE TRIGGER trg_pm_task_no_delete_completed;
ALTER TABLE diagnosis       ENABLE TRIGGER trg_diagnosis_immutable;

ALTER SEQUENCE work_order_number_seq RESTART;

COMMIT;
'@

# Places refer to their parent, and the database refuses to remove a parent while
# a child still points at it, so they go from the bottom of the tree up: whatever
# has nothing beneath it, again and again, until none are left.
$locationSql = @'
DO $$
DECLARE removed integer;
BEGIN
    LOOP
        DELETE FROM location l WHERE NOT EXISTS (SELECT 1 FROM location c WHERE c.parent_id = l.id);
        GET DIAGNOSTICS removed = ROW_COUNT;
        EXIT WHEN removed = 0;
    END LOOP;
END $$;
'@

if ($IncludeLocations) {
    $wipeSql = $wipeSql.Replace('--LOCATIONS--', $locationSql)
}
else {
    $wipeSql = $wipeSql.Replace('--LOCATIONS--', '')
}

function Invoke-Psql {
    param([string]$Sql, [switch]$File, [string]$What)

    $pgArgs = @(
        "--host=$($creds.host)", "--port=$($creds.port)", "--username=$($creds.superuser)",
        "--dbname=$($creds.database)", "--no-password", "--quiet", "--tuples-only", "--no-align")

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        if ($File) {
            $tmp = [System.IO.Path]::GetTempFileName()
            Set-Content -Path $tmp -Value $Sql -Encoding ascii
            $out = & $psql @pgArgs "--file=$tmp" 2>&1
            Remove-Item $tmp -Force
        }
        else {
            $out = & $psql @pgArgs "--command=$Sql" 2>&1
        }

        if ($LASTEXITCODE -ne 0) { throw "$What failed:`n$($out -join [Environment]::NewLine)" }
        return $out
    }
    finally { $ErrorActionPreference = $previous }
}

$counts = @"
SELECT 'machines ' || (SELECT count(*) FROM equipment) ||
       ', PM schedules ' || (SELECT count(*) FROM pm_schedule) ||
       ', PM tasks ' || (SELECT count(*) FROM pm_task) ||
       ', completed PMs ' || (SELECT count(*) FROM pm_completion) ||
       ', work orders ' || (SELECT count(*) FROM work_order) ||
       ', places ' || (SELECT count(*) FROM location)
"@

try {
    Set-Content -Path $passFile -Encoding ascii -Value "$($creds.host):$($creds.port):*:$($creds.superuser):$($creds.superpass)"
    $env:PGPASSFILE = $passFile

    Write-Host ""
    Write-Host "This installation holds:"
    Write-Host "    $((Invoke-Psql -Sql $counts -What 'counting the records') -join '')"
    Write-Host ""
    Write-Host "All of the machines above, and everything recorded against them, will be deleted."
    if ($IncludeLocations) {
        Write-Host "All of the places will be deleted too, so they can be entered afresh."
        Write-Host "Checklists, staff, settings and the licence are kept."
    }
    else {
        Write-Host "Places, checklists, staff, settings and the licence are kept."
    }
    Write-Host ""

    $answer = Read-Host "Type DELETE to go ahead, or anything else to stop"
    if ($answer -cne "DELETE") {
        Write-Host "Nothing was changed."
        return
    }

    # --- A backup first, and proof that it opens --------------------------------
    $backupDir = Join-Path $DataDir "backups"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $backup = Join-Path $backupDir ("pre-wipe-{0:yyyyMMdd-HHmmss}.dump" -f (Get-Date))

    Write-Host "Backing up to $backup ..."
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & $pgDump --format=custom --no-owner --no-privileges `
        "--host=$($creds.host)" "--port=$($creds.port)" "--username=$($creds.superuser)" `
        "--dbname=$($creds.database)" "--file=$backup" 2>&1 | Out-Null
    $dumpExit = $LASTEXITCODE
    & $pgRestore --list $backup 2>&1 | Out-Null
    $listExit = $LASTEXITCODE
    $ErrorActionPreference = $previous

    if ($dumpExit -ne 0 -or $listExit -ne 0 -or -not (Test-Path $backup) -or (Get-Item $backup).Length -eq 0) {
        throw "The backup did not complete, so nothing was deleted."
    }

    Write-Host ("Backup written and checked ({0} KB)." -f [math]::Round((Get-Item $backup).Length / 1KB))

    # --- The deletion, in one transaction ---------------------------------------
    Write-Host "Deleting..."
    Invoke-Psql -Sql $wipeSql -File -What "the deletion" | Out-Null

    # --- Proof ------------------------------------------------------------------
    $left = (Invoke-Psql -Sql $counts -What "counting what is left") -join ''
    $guards = (Invoke-Psql -Sql "SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgenabled <> 'O'" `
                            -What "checking the protection is back on") -join ''

    Write-Host ""
    Write-Host "Now: $left"
    if ($guards.Trim() -ne "0") {
        Write-Warning "Some database triggers are not enabled ($guards). Restore the backup and report this."
    }
    else {
        Write-Host "The database's protection against changing recorded work is back on."
    }

    Write-Host ""
    Write-Host "Done. To undo this, restore the backup:"
    Write-Host "    & '$(Join-Path (Split-Path -Parent $PgRoot) 'restore-database.ps1')' -PgRoot '$PgRoot' ``"
    Write-Host "        -CredentialsFile '$credentialsFile' -DumpFile '$backup' ``"
    Write-Host "        -BackupDir '$backupDir' -LogFile '$(Join-Path $DataDir 'restore.log')'"
}
finally {
    Remove-Item Env:\PGPASSFILE -ErrorAction SilentlyContinue
    if (Test-Path $passFile) { Remove-Item $passFile -Force }
}
