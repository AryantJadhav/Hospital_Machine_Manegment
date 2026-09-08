<#
.SYNOPSIS
    Installs Hospital PM on this machine, in every way that has gone wrong
    before, and fails the build if any of them stops behaving.

.DESCRIPTION
    Every installer bug this project has had was invisible to reading and only
    appeared on a real machine: a service registered against a database that
    was never created, a signing key readable by every local user, an
    uninstaller that deleted the hospital's records, an upgrade that refused
    itself. They were found by installing by hand, which does not scale and
    does not survive the next edit to HospitalPm.iss.

    One of them - the upgrade race - was NON-DETERMINISTIC. An earlier manual
    run passed it by winning a six-second race. That is the case for
    automating this: a person cannot re-run seven scenarios on every commit,
    and a person who runs them once cannot tell a pass from a lucky pass.

    Destructive by design. It installs, breaks, and uninstalls Hospital PM,
    including deleting its data directory. Intended for an ephemeral CI
    runner. It refuses to start if this machine already has Hospital PM on it,
    rather than eating someone's real installation.

.EXAMPLE
    pwsh installer\smoke-test.ps1 -Setup artifacts\installer\HospitalPM-Setup-1.0.0.exe
#>
[CmdletBinding()]
param(
    [string]$Setup = "artifacts\installer\HospitalPM-Setup-1.0.0.exe",

    # Not the 5000 default. A CI runner may have something of its own there,
    # and a smoke test that fails because the runner was busy teaches nothing.
    [int]$Port = 5080,

    [int]$DbPort = 5433,

    [string]$LogDir = "artifacts\smoke-logs"
)

$ErrorActionPreference = "Stop"

$AppName      = "Hospital PM"
$ServiceName  = "HospitalPM"
$PgService    = "HospitalPM_Postgres"
$InstallDir   = Join-Path $env:ProgramFiles $AppName
$DataDir      = Join-Path $env:ProgramData $AppName
$FailureFile  = Join-Path $InstallDir "install-failure.txt"
$UninstallExe = Join-Path $InstallDir "unins000.exe"

$Setup = (Resolve-Path $Setup).Path
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogDir = (Resolve-Path $LogDir).Path

$script:Failures = @()
$script:Scenario = "startup"
$script:FailuresAtScenarioStart = 0

# --- Reporting -------------------------------------------------------------
# ::error:: and ::group:: are GitHub Actions workflow commands. They are inert
# noise anywhere else, which is what makes this script runnable by hand when
# something needs debugging.

<#
    Dumps everything that explains a failed install, into the job log.

    Written after the first CI run failed and could not say why: the install
    reported that the database step had failed, and the log that would have
    named the reason lives in ProgramData, which nothing collected. Reading a
    failure should not require a second run with more logging bolted on.

    Copied into the log directory as well, so the uploaded artifact carries
    them, but printed inline first - the job log is where someone looks.
#>
function Show-Diagnostics([string]$Tag) {
    $sources = @(
        @{ Name = "install-failure.txt"; Path = $FailureFile },
        @{ Name = "setup-database.log";  Path = (Join-Path $DataDir "setup-database.log") },
        @{ Name = "restore.log";         Path = (Join-Path $DataDir "restore.log") }
    )

    $target = Join-Path $LogDir "$Tag-diagnostics"
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    foreach ($s in $sources) {
        if (-not (Test-PathQuiet $s.Path)) { continue }
        Write-Host "::group::$Tag - $($s.Name)"
        Get-Content $s.Path -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
        Write-Host "::endgroup::"
        Copy-Item $s.Path (Join-Path $target $s.Name) -ErrorAction SilentlyContinue
    }

    # PostgreSQL says why it would not start here and nowhere else.
    $pgLog = Join-Path $DataDir "pgdata\log"
    if (Test-PathQuiet $pgLog) {
        foreach ($f in @(Get-ChildItem $pgLog -File -ErrorAction SilentlyContinue |
                         Sort-Object LastWriteTime -Descending | Select-Object -First 2)) {
            Write-Host "::group::$Tag - pgdata/log/$($f.Name)"
            Get-Content $f.FullName -Tail 60 -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Host "  $_" }
            Write-Host "::endgroup::"
            Copy-Item $f.FullName (Join-Path $target $f.Name) -ErrorAction SilentlyContinue
        }
    }
}

function Write-Scenario([string]$Name) {
    # Whatever the scenario just ending left behind is the only evidence of why
    # it failed, and the next scenario is about to destroy it.
    if ($script:Failures.Count -gt $script:FailuresAtScenarioStart) {
        Show-Diagnostics ($script:Scenario -replace '[^A-Za-z0-9]+', '-')
    }
    $script:FailuresAtScenarioStart = $script:Failures.Count

    $script:Scenario = $Name
    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
}

function Assert-That([bool]$Condition, [string]$What) {
    if ($Condition) {
        Write-Host "    ok    $What"
    }
    else {
        Write-Host "::error::[$script:Scenario] $What"
        $script:Failures += "[$script:Scenario] $What"
    }
}

<#
    Asserts what Setup's own log says it did.

    An exit code of 7 only means Setup refused - it does not say why. All
    three preflight refusals return 7, so without this a check that began
    firing for the wrong reason would still pass. Reading the reason back is
    the difference between "it refused" and "it refused because of this".
#>
function Assert-LogSays([string]$LogName, [string]$Pattern, [string]$What) {
    $log = Join-Path $LogDir "$LogName.log"
    if (-not (Test-Path $log)) {
        Assert-That $false "$What (no log at $log)"
        return
    }
    Assert-That ((Get-Content $log -Raw) -match $Pattern) $What
}

# --- Machine state ---------------------------------------------------------

<#
    Test-Path that cannot abort the run.

    The data directory is deliberately locked to SYSTEM and Administrators,
    so probing it throws UnauthorizedAccessException rather than returning
    false - and with $ErrorActionPreference = Stop that ends the script four
    scenarios in, with a stack trace instead of a result. The elevation check
    below means this should never fire; it is here so that if it ever does,
    it fails one assertion rather than the whole run.
#>
function Test-PathQuiet([string]$Path) {
    return [bool](Test-Path $Path -ErrorAction SilentlyContinue)
}

function Get-ServiceState([string]$Name) {
    $s = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($s) { return $s.Status.ToString() }
    return "absent"
}

function Test-Installed {
    return (Test-Path $InstallDir) -or
           ((Get-ServiceState $ServiceName) -ne "absent") -or
           ((Get-ServiceState $PgService) -ne "absent")
}

<#
    Returns the HTTP status from /health, or $null.

    Retried, because the point of the health check is that the service comes
    up eventually - the installer has already waited for it, so this is
    confirming rather than waiting.
#>
function Get-Health([int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try {
            $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 `
                     -Uri "http://localhost:$Port/health"
            return [int]$r.StatusCode
        }
        catch { Start-Sleep -Seconds 2 }
    } while ((Get-Date) -lt $deadline)
    return $null
}

<#
    Serves the SPA, not just /health.

    /health answers 200 even when wwwroot cannot be resolved, which has
    already let a build ship with no UI at all. An installed service resolves
    its content root differently from a console run - CWD is System32 - so
    this is the deployment where that breaks.
#>
function Test-SpaServed {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 -Uri "http://localhost:$Port/"
        return ($r.StatusCode -eq 200 -and $r.Content -match '<script')
    }
    catch { return $false }
}

function Invoke-Setup {
    param([string[]]$Extra = @(), [string]$LogName)

    $log = Join-Path $LogDir "$LogName.log"
    $arguments = @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES",
        "/PORT=$Port", "/DBPORT=$DbPort",
        "/HOSPITAL=Sahyadri Hospital, Pune",
        "/ADMINUSER=admin", "/ADMINNAME=Dr S Deshmukh",
        "/ADMINPASSWORD=SmokeTest12345",
        "/LOG=$log"
    ) + $Extra

    return (Wait-ForProcess -FilePath $Setup -Arguments $arguments -What "Setup")
}

<#
    Runs a process and waits for THAT process, with a deadline.

    Not Start-Process -Wait, which also waits on children the process leaves
    behind. Setup ShellExec'd a URL as its last act, the browser never exited
    on a headless runner, and the harness sat there for thirty-five minutes
    while Setup itself had finished in twenty-six seconds. The installer no
    longer does that under /VERYSILENT, but a test harness should not be able
    to hang on it either.

    The deadline turns a stuck Setup into a reported failure instead of a job
    that runs until the runner's own timeout and explains nothing.
#>
function Wait-ForProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$What,
        [int]$TimeoutSeconds = 600
    )

    $p = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Assert-That $false "$What did not exit within $TimeoutSeconds seconds"
        try { $p.Kill($true) } catch { }
        return -1
    }
    return $p.ExitCode
}

<#
    Setup with nothing but the silent flags - no hospital name, no
    administrator, no ports.

    This is precisely what the application's own updater runs, because on an
    upgrade it has no answers to give and needs none. Every scenario above
    passes a full parameter set, which is why a real in-app update could
    never have worked and CI would never have said so.
#>
function Invoke-BareSetup {
    param([string]$LogName)

    $log = Join-Path $LogDir "$LogName.log"
    return (Wait-ForProcess -FilePath $Setup `
        -Arguments @("/VERYSILENT", "/NORESTART", "/SUPPRESSMSGBOXES", "/LOG=$log") `
        -What "Setup" -TimeoutSeconds 300)
}

function Invoke-Uninstall([switch]$RemoveData) {
    if (-not (Test-Path $UninstallExe)) { return $null }
    $arguments = @("/VERYSILENT", "/SUPPRESSMSGBOXES")
    if ($RemoveData) { $arguments += "/REMOVEDATA=1" }
    $code = Wait-ForProcess -FilePath $UninstallExe -Arguments $arguments -What "Uninstall"

    # The uninstaller hands the last of its own deletion to a detached process
    # and returns before it has finished.
    Start-Sleep -Seconds 12
    return $code
}

<#
    Puts the machine back to nothing, whatever state it is in.

    Called between scenarios so that one failure does not cascade into
    every scenario after it reporting a fault it did not cause.
#>
function Reset-Machine {
    Invoke-Uninstall -RemoveData | Out-Null

    foreach ($name in @($ServiceName, $PgService)) {
        if ((Get-ServiceState $name) -ne "absent") {
            & sc.exe stop $name   | Out-Null
            Start-Sleep -Seconds 8
            & sc.exe delete $name | Out-Null
        }
    }

    foreach ($dir in @($InstallDir, $DataDir)) {
        if (Test-PathQuiet $dir) {
            Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

<#
    Stops the services and removes the cluster's control file.

    A cluster that exists but cannot start. This is what a half-deleted data
    directory, an interrupted antivirus quarantine, or a disk error looks
    like to the installer, and it is the realistic way to make the database
    step fail after the payload is already on disk - the phase where Inno
    cannot report failure at all.
#>
function Invoke-BreakCluster {
    & sc.exe stop $ServiceName | Out-Null
    & sc.exe stop $PgService   | Out-Null
    Start-Sleep -Seconds 12

    # Deliberately non-throwing. If the file cannot be removed the scenario
    # should report that as a failed assertion, not abort the whole run with
    # a stack trace two scenarios from the end.
    $control = Join-Path $DataDir "pgdata\global\pg_control"
    Remove-Item $control -Force -ErrorAction SilentlyContinue
    return (-not (Test-PathQuiet $control))
}

# ---------------------------------------------------------------------------
# Scenarios
# ---------------------------------------------------------------------------

Write-Host "Hospital PM installer smoke test"
Write-Host "  setup   : $Setup"
Write-Host "  app port: $Port"
Write-Host "  db port : $DbPort"

Write-Scenario "Machine starts clean"

# Registering services, reading an ACL-locked folder and stopping a stuck
# service all need administrator rights. Without them this script does not
# fail honestly - it gets four scenarios in and dies on an access-denied
# probing the data directory, which is exactly what it is supposed to be
# checking is locked. Refuse up front instead.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([System.Security.Principal.WindowsPrincipal]$identity).IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "::error::This script must run elevated. It manages Windows services and reads a folder locked to SYSTEM and Administrators."
    exit 2
}
Assert-That $true "running elevated"

if (Test-Installed) {
    # Not a test failure - a refusal. Running the rest would delete an
    # installation this script did not create.
    Write-Host "::error::Hospital PM is already installed on this machine. This script is destructive and will not run against an existing installation."
    exit 2
}
Assert-That $true "no existing installation"

foreach ($p in @($Port, $DbPort)) {
    $busy = [bool]([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().
        GetActiveTcpListeners() | Where-Object { $_.Port -eq $p })
    if ($busy) {
        Write-Host "::error::Port $p is already in use on this machine, so the port scenarios cannot mean anything."
        exit 2
    }
}
Assert-That $true "ports $Port and $DbPort are free"

# --- 1. The app port is taken ---------------------------------------------
# Must refuse BEFORE writing anything. This is one of only two places Inno
# can still return a real exit code, so it is the one that has to work.
Write-Scenario "Refuses when the app port is in use, and writes nothing"

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
try {
    $code = Invoke-Setup -LogName "01-app-port-busy"
}
finally { $listener.Stop() }

Assert-That ($code -eq 7)                    "exit 7 (was $code)"
Assert-LogSays "01-app-port-busy" "already using port $Port" "refused for the app port, not something else"
Assert-That (-not (Test-Path $InstallDir))   "nothing installed"
Assert-That (-not (Test-Installed))          "no services registered"

# --- 2. The database port is taken ----------------------------------------
Write-Scenario "Refuses when the database port is in use, and writes nothing"

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $DbPort)
$listener.Start()
try {
    $code = Invoke-Setup -LogName "02-db-port-busy"
}
finally { $listener.Stop() }

Assert-That ($code -eq 7)                    "exit 7 (was $code)"
Assert-LogSays "02-db-port-busy" "Port $DbPort is already in use" "refused for the database port"
Assert-That (-not (Test-Path $InstallDir))   "nothing installed"

# --- 3. A cluster whose credentials are gone ------------------------------
# The application's database password lives only in db.json and cannot be
# recovered from the cluster. Caught before the payload lands, where it can
# still be reported.
Write-Scenario "Refuses an orphaned cluster whose db.json is missing"

New-Item -ItemType Directory -Force -Path (Join-Path $DataDir "pgdata") | Out-Null
Set-Content -Path (Join-Path $DataDir "pgdata\PG_VERSION") -Value "17" -Encoding ascii

$code = Invoke-Setup -LogName "03-orphaned-cluster"

Assert-That ($code -eq 7)                    "exit 7 (was $code)"
Assert-LogSays "03-orphaned-cluster" "db\.json" "refused because the credentials file is missing"
Assert-That (-not (Test-Path $InstallDir))   "nothing installed"

Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue

# Asserted, not assumed. This scenario plants a fake PG_VERSION, and if it
# survives, the NEXT scenario's install sees an existing cluster with no
# credentials and fails within seconds - reporting a database fault that this
# scenario caused. Silent cleanup is how a test suite blames the wrong thing.
Assert-That (-not (Test-PathQuiet $DataDir)) "the planted cluster was cleaned up"

# --- 4. A clean install ----------------------------------------------------
Write-Scenario "Installs clean, and actually serves"

$code = Invoke-Setup -LogName "04-clean-install"
$health = Get-Health

Assert-That ($code -eq 0)                          "exit 0 (was $code)"
Assert-That (-not (Test-Path $FailureFile))        "no install-failure.txt"
Assert-That ((Get-ServiceState $ServiceName) -eq "Running") "$ServiceName is running"
Assert-That ((Get-ServiceState $PgService) -eq "Running")   "$PgService is running"
Assert-That ($health -eq 200)                      "/health answered 200 (was $health)"
Assert-That (Test-SpaServed)                       "the SPA is served from /"

# The settings file must be inside the locked folder, never beside the binary.
# An install test once found the JWT signing key in Program Files, readable by
# every local user, and anyone who can read it can mint an admin token.
Assert-That (Test-PathQuiet (Join-Path $DataDir "appsettings.json")) "settings live in ProgramData"
Assert-That (Test-PathQuiet (Join-Path $DataDir "keys"))             "the signing key folder is in ProgramData"
Assert-That (-not (Test-PathQuiet (Join-Path $InstallDir "data")))   "nothing written beside the binary"

# --- 5. Upgrading over itself, twice --------------------------------------
# The regression this exists for. sc stop returns when it has SIGNALLED a
# service, not when the process is gone; Setup used to sleep a fixed six
# seconds, then find its own PostgreSQL still holding the database port and
# refuse the upgrade blaming "another PostgreSQL". Twice, because once is
# how that bug passed in the first place.
Write-Scenario "Upgrades over itself without losing the race"

foreach ($attempt in 1..2) {
    $code = Invoke-Setup -LogName "05-upgrade-$attempt"
    $health = Get-Health

    Assert-That ($code -eq 0)                   "upgrade ${attempt}: exit 0 (was $code)"
    Assert-That (-not (Test-Path $FailureFile)) "upgrade ${attempt}: no install-failure.txt"
    Assert-That ($health -eq 200)               "upgrade ${attempt}: /health answered 200 (was $health)"
}

# --- 5b. The upgrade the application itself performs ----------------------
# The updater launches Setup with the silent flags and nothing else. It has
# no hospital name or password to pass and should need none: both already
# exist in the database it is upgrading.
#
# This once put a modal error box on the machine - MsgBox is not covered by
# /SUPPRESSMSGBOXES - and waited for a click. In session 0, where the
# service runs, that click can never come, so an in-app update stopped the
# service and hung there.
Write-Scenario "Upgrades with no answers, the way the updater does"

$code = Invoke-BareSetup -LogName "05b-bare-upgrade"
$health = Get-Health

Assert-That ($code -eq 0)                   "bare upgrade: exit 0 (was $code)"
Assert-That (-not (Test-Path $FailureFile)) "bare upgrade: no install-failure.txt"
Assert-That ($health -eq 200)               "bare upgrade: /health answered 200 (was $health)"

# The name on every printed report. An upgrade is not told it and must not
# lose it.
$settingsAfter = Get-Content "$DataDir\appsettings.json" -Raw | ConvertFrom-Json
Assert-That ($settingsAfter.FirstRun.HospitalName -eq "Sahyadri Hospital, Pune") `
    "bare upgrade: kept the hospital name (was '$($settingsAfter.FirstRun.HospitalName)')"

# The port matters more than the name. The harness installs on $Port, not on
# the 5000 default, so a bare upgrade that fell back to the default would
# move the app - and every bookmark, shortcut and ward tablet with it.
Assert-That ($settingsAfter.Urls -eq "http://+:$Port") `
    "bare upgrade: still serving on $Port (Urls is '$($settingsAfter.Urls)')"

$dbAfter = Get-Content "$DataDir\db.json" -Raw | ConvertFrom-Json
Assert-That ([int]$dbAfter.port -eq $DbPort) `
    "bare upgrade: database still on $DbPort (was $($dbAfter.port))"

# --- 5c. A fresh silent install with nothing to go on ---------------------
# The other half: with no existing database there IS no hospital name or
# administrator to inherit, so Setup must refuse - with an exit code, in a
# log, and without a window.
Write-Scenario "Refuses a bare install on a clean machine, without a dialog"

Reset-Machine

$code = Invoke-BareSetup -LogName "05c-bare-clean"

Assert-That ($code -ne 0)                   "bare clean install: refused with a non-zero code (was $code)"
Assert-That (-not (Test-Path $FailureFile)) "bare clean install: nothing was installed"

# Put the machine back for the scenarios that follow.
$code = Invoke-Setup -LogName "05c-restore"
Assert-That ($code -eq 0) "restored the install for later scenarios (was $code)"

# --- 6. A failure after the payload has landed ----------------------------
# The phase where Inno reports success no matter what. The exit code cannot
# be made non-zero here, so install-failure.txt IS the result, and it has to
# be present, accurate, and readable without elevating.
Write-Scenario "Reports a post-copy failure it cannot fail the install for"

Assert-That (Invoke-BreakCluster) "the cluster's pg_control was removed"

$code = Invoke-Setup -LogName "06-broken-cluster"

# Asserted rather than tolerated. If a future Inno ever makes this non-zero,
# this line fails and the workaround below can be deleted - which is the
# only way anyone would find out.
Assert-That ($code -eq 0) "exit 0, as Inno cannot fail after file copying (was $code)"
Assert-That (Test-Path $FailureFile) "install-failure.txt was written"

# All of this is inside the existence check on purpose. Get-Acl on a file that
# is not there throws, and with $ErrorActionPreference = Stop that ends the run
# - taking both uninstall scenarios with it. The scenario most likely to fail
# must not be the one that stops the rest from being reported.
if (Test-PathQuiet $FailureFile) {
    $report = Get-Content $FailureFile -Raw
    Assert-That ($report -match 'database could not be set up') "the report names the database step"
    Assert-That ($report -match 'setup-database\.log')          "the report says where to look"

    # Readable by the person who has to read it. The report first shipped
    # inside the data directory, which is locked to SYSTEM and Administrators
    # - written correctly and impossible to open.
    $readableByUsers = @((Get-Acl $FailureFile).Access | Where-Object {
        $_.IdentityReference -match 'BUILTIN\\Users|Everyone|Authenticated Users' -and
        $_.AccessControlType -eq 'Allow'
    }).Count -gt 0
    Assert-That $readableByUsers "install-failure.txt is readable without elevating"
}

# --- 7. Uninstalling leaves nothing ---------------------------------------
# With a failure file present, which is the case that used to leave an empty
# Hospital PM folder in Program Files forever: Inno will not remove the
# install directory once a file it has no record of has lived there.
Write-Scenario "Uninstalls completely, even after a failed install"

$code = Invoke-Uninstall -RemoveData

Assert-That ($code -eq 0)                                     "uninstall exit 0 (was $code)"
Assert-That (-not (Test-Path $InstallDir))                    "no install directory left"
Assert-That (-not (Test-PathQuiet $DataDir))                       "no data directory left"
Assert-That ((Get-ServiceState $ServiceName) -eq "absent")    "$ServiceName deregistered"
Assert-That ((Get-ServiceState $PgService) -eq "absent")      "$PgService deregistered"

# --- 8. Uninstalling KEEPS the data unless asked -------------------------
# The one mistake in an uninstaller that cannot be undone. An earlier version
# asked with a message box defaulting to No, and a silent uninstall deleted
# the folder anyway - on a real machine, the hospital's entire database.
Write-Scenario "Keeps the hospital's data when removal was not asked for"

$code = Invoke-Setup -LogName "08-reinstall-for-retention"
Assert-That ($code -eq 0) "reinstalled for the retention check (exit $code)"

$code = Invoke-Uninstall     # deliberately WITHOUT /REMOVEDATA=1

Assert-That ($code -eq 0)                     "uninstall exit 0 (was $code)"
Assert-That (-not (Test-Path $InstallDir))    "no install directory left"
Assert-That (Test-PathQuiet $DataDir)              "the data directory SURVIVED"
Assert-That (Test-PathQuiet (Join-Path $DataDir "pgdata\PG_VERSION")) "the database itself survived"

# The last scenario has no successor to trigger its dump, and Reset-Machine is
# about to delete everything that would explain it.
if ($script:Failures.Count -gt $script:FailuresAtScenarioStart) {
    Show-Diagnostics ($script:Scenario -replace '[^A-Za-z0-9]+', '-')
}

Reset-Machine

# ---------------------------------------------------------------------------
Write-Host ""
if ($script:Failures.Count -gt 0) {
    Write-Host "FAILED - $($script:Failures.Count) assertion(s):" -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "All installer scenarios passed." -ForegroundColor Green
exit 0
