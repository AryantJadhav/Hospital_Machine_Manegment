; Hospital PM — Windows installer
;
; Target: a hospital PC with no IT staff. Every decision here assumes the
; person running it will not read documentation, cannot be asked to edit a
; config file, and will not know what a service is.
;
;
; PostgreSQL is bundled. The hospital is never asked for database details and
; never sees a password: the installer creates its own cluster on a private
; port, generates credentials, and locks them away. That is what makes the
; five-minute install real - the alternative is talking a ward clerk through
; installing a database server over the phone.
;
;
; --- On failure reporting, which is not obvious and was measured -----------
;
; Inno Setup can only fail an installation BEFORE file copying begins. This
; was measured against Inno 6.7.3 rather than assumed, because everything
; below depends on it:
;
;   InitializeSetup returns False .................. exit 1, nothing written
;   PrepareToInstall returns a message ............. exit 7, nothing written
;   CurStepChanged(ssInstall) aborts ............... exit 3, rolled back
;   [Files] AfterInstall raises or aborts .......... exit 0, files left
;   [Run] entry's program exits non-zero ........... exit 0, files left
;   [Run] BeforeInstall raises or aborts ........... exit 0, files left,
;                                                    and the entry still runs
;   CurStepChanged(ssPostInstall) raises or aborts . exit 0, files left
;
; Stage order is ssInstall -> [Files] AfterInstall -> [Run] -> ssPostInstall.
;
; So everything after the payload is extracted - creating the database
; cluster, writing settings, registering and starting the service - runs in a
; phase where Inno reports success no matter what happens. An earlier version
; of this file relied on RaiseException from a [Run] entry's BeforeInstall to
; stop the install when the database step had failed. It stops nothing: the
; exception is logged as an internal error, the [Run] entry executes anyway,
; and Setup exits 0. A silent install could leave a hospital with a registered
; service, no settings file and no database, and report success to whoever ran
; it.
;
; Two things follow, and both are done below:
;
;   1. Every check that CAN be made before file copying is made in
;      PrepareToInstall, where refusing costs the hospital nothing and returns
;      a real exit code. Ports, an orphaned cluster.
;
;   2. What genuinely cannot move - the database, the service, the first start
;      - runs in ssPostInstall with every exit code checked, stops at the
;      first failure, and writes the reason to install-failure.txt in the
;      install directory. Since Inno's exit code cannot be made non-zero
;      there, that file IS the machine-readable result:
;
;        HospitalPM-Setup.exe /VERYSILENT ...
;        if exist "%ProgramFiles%\Hospital PM\install-failure.txt" ( ... )
;
;      An unattended deployment must test for it, because the exit code will
;      be 0 either way. Said on the Ready page and in docs/REVIEW.md rather
;      than left to be discovered.
;
; Build:  ISCC.exe installer\HospitalPm.iss /DAppVersion=1.0.0 /DSourceDir=<published win-x64 folder>

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\win-x64"
#endif

#define AppName "Hospital PM"
#define AppPublisher "Hospital PM"
#define ServiceName "HospitalPM"
#define ServiceDisplay "Hospital PM"
#define ExeName "hospitalpm.exe"
#define PgServiceName "HospitalPM_Postgres"

#ifndef PgSourceDir
  #define PgSourceDir "..\artifacts\pgsql"
#endif

[Setup]
; Never change AppId. It is how Windows recognises an upgrade rather than a
; second parallel installation.
AppId={{8E3C1E60-2C2B-4D3E-9E2E-6C4D1B7A9F41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=HospitalPM-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The service must be registered and the firewall opened, neither of which a
; standard user can do. Asking up front beats failing halfway.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayName={#AppName}
; A hospital that reinstalls after a support call must not be told the
; installer is older than what is there.
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; No RestartManager. Its scan runs before PrepareToInstall gets a chance to
; stop our services, so it finds them holding our own files, cannot close a
; Windows Service, and asks what to do - a question a silent install answers
; with Abort. That is an upgrade failing on exactly the machine that most
; needs one. PrepareToInstall stops both services itself, which is the only
; thing the scan would have been for.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; PostgreSQL, trimmed to bin, lib and share. Its own subdirectory so an
; upgrade replaces it wholesale and nothing here is confused with ours.
Source: "{#PgSourceDir}\*"; DestDir: "{app}\pgsql"; Flags: ignoreversion recursesubdirs createallsubdirs

; Database setup lives in a script rather than in this file's [Code] section:
; it is the most failure-prone part of the install, and a script can be run
; and debugged on its own.
Source: "setup-database.ps1"; DestDir: "{app}"; Flags: ignoreversion

; Restore. Shipped alongside, and runs outside the application because
; a process connected to the database cannot drop and recreate it.
Source: "restore-database.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; Data lives outside the install directory so uninstalling the program cannot
; delete the hospital's backups, licence or configuration.
Name: "{commonappdata}\{#AppName}"
Name: "{commonappdata}\{#AppName}\backups"
; The JWT signing key. It belongs inside the locked folder rather than beside
; the binary: anyone who can read it can forge a token for any user, and
; Program Files grants BUILTIN\Users read by default.
Name: "{commonappdata}\{#AppName}\keys"
; The database cluster. Outside the install directory for the same reason as
; everything else here: uninstalling the program must not delete the records.
Name: "{commonappdata}\{#AppName}\pgdata"

[Icons]
Name: "{group}\Open {#AppName}"; Filename: "http://localhost:{code:GetPort}/"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Run]
; Everything that used to be here now runs from InstallSteps in [Code], so
; that each step's exit code is actually looked at. Inno discards a [Run]
; entry's exit code, which made a failed database setup indistinguishable
; from a successful one.
;
; This entry is the exception: it is the Finished page's checkbox, it runs
; after everything else, and there is nothing to check. Suppressed when the
; install failed, so a broken system does not offer to open a page that will
; not load.
;
; skipifsilent is NOT optional here, and its absence was a real bug. A
; postinstall entry still EXECUTES under /VERYSILENT - the flag only governs
; the checkbox on a Finished page that a silent install never shows. So an
; unattended install opened a browser on whoever's session it could find,
; which for an SCCM or Intune rollout running as LocalSystem is meaningless at
; best. Caught by the CI smoke test: Setup finished, ShellExec'd the URL on a
; headless runner, and the harness then waited thirty-five minutes on a child
; process that was never going to exit.
Filename: "http://localhost:{code:GetPort}/"; Flags: shellexec postinstall nowait skipifsilent; \
  Description: "Open {#AppName} now"; Check: InstallSucceeded

[UninstallRun]
; Stop before deleting, or the files are locked and the uninstall leaves a
; broken service registered.
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewallRule"

[Code]
type
  // Layout must match MEMORYSTATUSEX exactly. Two DWORDs then eight 64-bit
  // fields, which happens to need no padding on x64.
  TMemoryStatusEx = record
    dwLength: Cardinal;
    dwMemoryLoad: Cardinal;
    ullTotalPhys: Int64;
    ullAvailPhys: Int64;
    ullTotalPageFile: Int64;
    ullAvailPageFile: Int64;
    ullTotalVirtual: Int64;
    ullAvailVirtual: Int64;
    ullAvailExtendedVirtual: Int64;
  end;

function GlobalMemoryStatusEx(var Buffer: TMemoryStatusEx): Boolean;
  external 'GlobalMemoryStatusEx@kernel32.dll stdcall';

var
  HospitalPage: TInputQueryWizardPage;
  AdminPage: TInputQueryWizardPage;
  LicencePage: TInputFileWizardPage;
  PortPage: TInputQueryWizardPage;

  // Empty means nothing has failed yet. Set once, by the first step that
  // fails; every step afterwards is skipped.
  FailureReason: String;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\{#AppName}');
end;

/// Pulls one string field out of the credentials JSON that setup-database.ps1
/// wrote. A deliberately small reader rather than a JSON parser: the file is
/// machine-written, one level deep, and its values are generated
/// alphanumerics, so there is nothing to escape and nothing to nest.
function ReadJsonField(const Json, Field: String): String;
var
  Marker: String;
  Start, Finish: Integer;
begin
  Result := '';
  Marker := '"' + Field + '":';

  Start := Pos(Marker, Json);
  if Start = 0 then
    Exit;

  Start := Start + Length(Marker);

  // Skip whitespace and the opening quote.
  while (Start <= Length(Json)) and
        ((Json[Start] = ' ') or (Json[Start] = #9) or (Json[Start] = '"')) do
    Start := Start + 1;

  Finish := Start;
  while (Finish <= Length(Json)) and
        (Json[Finish] <> '"') and (Json[Finish] <> ',') and
        (Json[Finish] <> #13) and (Json[Finish] <> #10) and
        (Json[Finish] <> '}') do
    Finish := Finish + 1;

  Result := Trim(Copy(Json, Start, Finish - Start));
end;

/// The hospital name the previous install was given, or empty.
///
/// Read before the settings file is rewritten, because Setup deletes and
/// recreates it. Without this an upgrade that was not told /HOSPITAL would
/// silently blank the name that appears on printed reports - which is
/// every upgrade the in-app updater performs.
function ExistingHospitalName(): String;
var
  Existing: AnsiString;
begin
  Result := '';
  if not FileExists(DataDir + 'ppsettings.json') then
    Exit;

  if LoadStringFromFile(DataDir + 'ppsettings.json', Existing) then
    Result := ReadJsonField(String(Existing), 'HospitalName');
end;

/// The port the previous install serves on, or empty.
///
/// Parsed out of "Urls": "http://+:5000" in the settings Setup itself wrote.
///
/// Inherited for the same reason as the hospital name, and it matters more:
/// an upgrade launched by the in-app updater passes no /PORT, so without this
/// it would quietly move a hospital off the port they chose and onto 5000 -
/// breaking the Start Menu shortcut, every bookmark, and every tablet on the
/// ward at once.
function ExistingAppPort(): String;
var
  Existing: AnsiString;
  Urls: String;
  Colon: Integer;
begin
  Result := '';
  if not FileExists(DataDir + 'ppsettings.json') then
    Exit;

  if not LoadStringFromFile(DataDir + 'ppsettings.json', Existing) then
    Exit;

  Urls := ReadJsonField(String(Existing), 'Urls');
  if Urls = '' then
    Exit;

  // http://+:5000 - take everything after the last colon.
  Colon := Length(Urls);
  while (Colon > 0) and (Urls[Colon] <> ':') do
    Colon := Colon - 1;

  if Colon > 0 then
    Result := Trim(Copy(Urls, Colon + 1, Length(Urls) - Colon));
end;

/// The port the existing bundled database actually listens on, or empty.
///
/// From db.json, which is what the cluster was created with. Guessing 5433 on
/// an upgrade would check the wrong port for conflicts and point the database
/// scripts at a cluster that is not there.
function ExistingDbPort(): String;
var
  Credentials: AnsiString;
begin
  Result := '';
  if not FileExists(DataDir + '\db.json') then
    Exit;

  if LoadStringFromFile(DataDir + '\db.json', Credentials) then
    Result := Trim(ReadJsonField(String(Credentials), 'port'));
end;
procedure InitializeWizard;
begin
  // Nothing is asked about the database. It is created by the installer, on a
  // port nobody needs to know, with credentials nobody needs to see.
  HospitalPage := CreateInputQueryPage(wpSelectDir,
    'Hospital', 'Which hospital is this?',
    'The name appears in the application and on printed reports.');
  HospitalPage.Add('Hospital name', False);
  HospitalPage.Values[0] := ExpandConstant('{param:HOSPITAL|}');

  AdminPage := CreateInputQueryPage(HospitalPage.ID,
    'Administrator', 'Who will manage the system?',
    'This account can add staff, import equipment and see everything.' + #13#10 +
    'Write the password down before continuing - it cannot be recovered from here.');
  AdminPage.Add('Full name', False);
  AdminPage.Add('Username', False);
  AdminPage.Add('Password', True);
  AdminPage.Add('Confirm password', True);
  AdminPage.Values[0] := ExpandConstant('{param:ADMINNAME|}');
  AdminPage.Values[1] := ExpandConstant('{param:ADMINUSER|admin}');
  AdminPage.Values[2] := ExpandConstant('{param:ADMINPASSWORD|}');
  AdminPage.Values[3] := ExpandConstant('{param:ADMINPASSWORD|}');

  // Optional, because a pilot install runs before anyone has issued one.
  LicencePage := CreateInputFilePage(AdminPage.ID,
    'Licence', 'Do you have a licence file?',
    'If one was emailed to you, select it. You can skip this and install it later' + #13#10 +
    'from the Licence page - nothing stops working without it.');
  LicencePage.Add('Licence file (optional):', 'Licence files|*.licence|All files|*.*', '.licence');
  LicencePage.Values[0] := ExpandConstant('{param:LICENCE|}');

  PortPage := CreateInputQueryPage(LicencePage.ID,
    'Network', 'Which port should Hospital PM use?',
    'Staff will reach the system at http://<this computer>:<port>/ from a browser or a phone.' + #13#10 +
    'The default suits almost every installation.');
  PortPage.Add('Port', False);

  // Defaults come from the command line so an unattended install works.
  // /VERYSILENT skips these pages, which means whatever is set here is what
  // gets used - so this is also how SCCM and Intune drive it:
  //
  //   HospitalPM-Setup.exe /VERYSILENT /PORT=5000 /DBPORT=5433
  //     /HOSPITAL="Sahyadri Hospital, Pune" /ADMINUSER=admin
  //     /ADMINNAME="Dr S Deshmukh" /ADMINPASSWORD=... /LICENCE=<file>
  //
  // Values containing spaces MUST be quoted. Inno reads {param:} up to the
  // next space, so /HOSPITAL=Sahyadri Hospital silently becomes "Sahyadri" -
  // and a hospital name and a person's name almost always contain spaces.
  // The wizard is unaffected; this only bites an unattended install.
  // An explicit /PORT wins. Failing that, whatever this computer is already
  // serving on - an upgrade must not move a hospital off the port they
  // chose. 5000 only for a genuinely new install.
  PortPage.Values[0] := ExpandConstant('{param:PORT|}');
  if Trim(PortPage.Values[0]) = '' then
    PortPage.Values[0] := ExistingAppPort();
  if Trim(PortPage.Values[0]) = '' then
    PortPage.Values[0] := '5000';
end;

function GetPort(Param: String): String;
begin
  Result := PortPage.Values[0];
end;

/// The bundled PostgreSQL's port. Not 5432: a machine that already runs
/// PostgreSQL must keep working, and ours has to sit beside it rather than
/// fight it for the port.
function GetDbPort(Param: String): String;
begin
  Result := Trim(ExpandConstant('{param:DBPORT|}'));

  // The cluster that exists beats any default. Guessing 5433 on a machine
  // whose cluster is elsewhere checks the wrong port for conflicts and
  // points the database scripts at nothing.
  if Result = '' then
    Result := ExistingDbPort();
  if Result = '' then
    Result := '5433';
end;


/// Where a failed install explains itself.
///
/// The install directory, NOT the data directory. The data directory is
/// locked to SYSTEM and Administrators because it holds the database
/// password, which means a report written there cannot be opened by the
/// person reading it without elevating first - and someone whose install has
/// just failed should not also have to work out how to get at the
/// explanation. An install-test caught exactly that: the file was written,
/// correctly, and was unreadable.
///
/// Program Files is readable by every local user, the folder is guaranteed to
/// exist by the time anything can fail here, and the report contains no
/// secrets - a reason, a step, and the paths of three logs.
function FailureFile: String;
begin
  Result := ExpandConstant('{app}\install-failure.txt');
end;

/// True while nothing has failed.
function InstallSucceeded: Boolean;
begin
  Result := FailureReason = '';
end;

/// Runs a program to completion and reports whether it exited 0.
///
/// Exec's own Boolean result only says the process could be STARTED; the exit
/// code comes back in ResultCode and is the part that matters. Conflating the
/// two is how a failed step reads as a successful one.
function RunChecked(const Exe, Params: String; var Code: Integer): Boolean;
begin
  Code := -1;
  Result := Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

/// Runs a PowerShell command and returns its exit code, or -1 if PowerShell
/// itself could not be started.
function RunPowerShell(const Command: String): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
              '-NoProfile -ExecutionPolicy Bypass -Command "' + Command + '"',
              '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := -1
  else
    Result := Code;
end;

/// Waits for both of our services to actually reach Stopped.
///
/// Replaces a fixed Sleep, which was a guess and lost the race often enough
/// to matter. An install test caught it: Setup stopped its own PostgreSQL,
/// waited six seconds, found port 5433 still held by it, and refused the
/// upgrade with a message blaming "another PostgreSQL". That is a
/// non-deterministic failure to upgrade the very machine already running the
/// product, and the same race previously risked the worse outcome of
/// replacing binaries under a live postgres.exe.
///
/// sc stop returns as soon as it has SIGNALLED the service, never when the
/// process has gone, so something has to wait. A service that does not exist
/// reports no status and counts as stopped, which is the fresh-install case.
function WaitForServicesStopped(const Seconds: String): Boolean;
begin
  Result := RunPowerShell(
    '$deadline = (Get-Date).AddSeconds(' + Seconds + '); ' +
    'do { ' +
    '  $running = @(''{#ServiceName}'', ''{#PgServiceName}'') | ForEach-Object { ' +
    '    (Get-Service $_ -ErrorAction SilentlyContinue).Status } | ' +
    '    Where-Object { $_ -and $_ -ne ''Stopped'' }; ' +
    '  if (-not $running) { exit 0 } ' +
    '  Start-Sleep -Seconds 1 ' +
    '} while ((Get-Date) -lt $deadline); ' +
    'exit 1') = 0;
end;

/// True when something is still listening on that TCP port after a grace
/// period.
///
/// GetActiveTcpListeners rather than Get-NetTCPConnection: it is a BCL call
/// that needs no elevation and no NetTCPIP module, so it behaves the same on
/// a stripped-down hospital image.
///
/// Retried rather than sampled once. Even after the SCM reports a service
/// stopped, its listening socket can take a moment to disappear, and a
/// single unlucky sample turns into a refusal to install. Ten seconds costs
/// nothing on the only path that reaches it - a machine that is genuinely
/// about to be refused.
///
/// Exit 2 means in use, 0 means free, and anything else means the question
/// could not be answered - which is NOT treated as in-use, because refusing
/// an install over a check that did not run is worse than not checking.
function PortInUse(const Port: String): Boolean;
var
  Attempt: Integer;
begin
  Result := False;
  for Attempt := 1 to 5 do
  begin
    if RunPowerShell(
         '$listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()' +
         '.GetActiveTcpListeners() | Where-Object { $_.Port -eq ' + Port + ' }; ' +
         'if ($listeners) { exit 2 } else { exit 0 }') <> 2 then
      Exit;

    Result := True;
    if Attempt < 5 then
      Sleep(2000);
  end;
end;

/// Refuses to install on a machine that cannot run this.
///
/// Checked before anything is written, because the alternative is a hospital
/// discovering it during a ward round. The numbers are what PostgreSQL and a
/// .NET service actually need with room for the database to grow, not
/// aspirational minimums.
function InitializeSetup: Boolean;
var
  FreeMb, TotalMb: Int64;
  MemoryMb: Int64;
  Memory: TMemoryStatusEx;
  Problem: String;
begin
  Result := True;
  Problem := '';

  // Windows 10 1809 is where the APIs this is built against are reliable, and
  // is also the oldest thing still receiving updates on hospital hardware.
  if not IsWin64 then
    Problem := 'Hospital PM needs 64-bit Windows. This machine is running a 32-bit version.'
  else if (GetWindowsVersion shr 24) < 10 then
    Problem := 'Hospital PM needs Windows 10 or newer, or Windows Server 2016 or newer.';

  if Problem = '' then
  begin
    Memory.dwLength := SizeOf(Memory);

    // A reading we could not take is not a reason to refuse. Blocking an
    // install over a failed API call would be worse than not checking: the
    // machine is probably fine, and the operator has no way to argue with it.
    if GlobalMemoryStatusEx(Memory) and (Memory.ullTotalPhys > 0) then
    begin
      MemoryMb := Memory.ullTotalPhys div (1024 * 1024);

      // 3500 rather than 4096: a 4 GB machine reports slightly less once
      // firmware and integrated graphics have taken their share, and refusing
      // one of those would be pedantry.
      if MemoryMb < 3500 then
        Problem := 'Hospital PM needs at least 4 GB of memory. This machine has about ' +
                   IntToStr(MemoryMb) + ' MB.';
    end;
  end;

  if Problem = '' then
  begin
    // PostgreSQL, the application and room for a hospital's database and a
    // fortnight of backups.
    // GetSpaceOnDisk64 reports bytes and takes no units flag - that is the
    // older GetSpaceOnDisk, whose Cardinal results overflow on a modern disk.
    if GetSpaceOnDisk64(ExpandConstant('{sd}\'), FreeMb, TotalMb) then
    begin
      FreeMb := FreeMb div (1024 * 1024);
      if FreeMb < 5000 then
        Problem := 'Hospital PM needs about 5 GB of free space. Drive ' +
                   ExpandConstant('{sd}') + ' has ' + IntToStr(FreeMb) + ' MB free.';
    end;
  end;

  if Problem <> '' then
  begin
    SuppressibleMsgBox(Problem + #13#10#13#10 + 'Setup cannot continue.',
                       mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

/// Stops both services, then refuses the install if this machine is not in a
/// state where it can succeed.
///
/// This is the last point at which refusing is free: returning a message here
/// exits with code 7 and writes nothing. Every check that can be made without
/// the payload extracted belongs here rather than later, because later there
/// is no way to fail at all.
/// Whether this computer already has a Hospital PM database.
///
/// db.json is the right marker rather than the install directory: it holds
/// the application database password, it lives in ProgramData which
/// survives an uninstall, and it is exactly what an upgrade reuses. A
/// machine with it has a hospital name and an administrator already, so
/// Setup must not ask for either.
function IsUpgrade(): Boolean;
begin
  Result := FileExists(DataDir + '\db.json');
end;


function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  AppPort, DbPort: String;
begin
  Result := '';

  // Everything the wizard would have insisted on, for an install nobody is
  // watching. Checked here rather than in NextButtonClick because a
  // returned string is an exit code and a log line, while a MsgBox on an
  // unattended machine is a hang.
  //
  // Only for a FRESH install. An upgrade already has a hospital name and
  // an administrator in its database, and demanding them again is what
  // stopped the in-app updater from ever working: it launches Setup with
  // /VERYSILENT and no answers, because it has none to give.
  if WizardSilent and (not IsUpgrade) then
  begin
    if Trim(HospitalPage.Values[0]) = '' then
    begin
      Result := 'This is a new installation and no hospital name was given.' + #13#10#13#10 +
                'Run Setup again with /HOSPITAL="<name>", or run it without /VERYSILENT ' +
                'and fill the pages in.' + #13#10#13#10 +
                'Nothing has been installed.';
      Exit;
    end;

    if Trim(AdminPage.Values[1]) = '' then
    begin
      Result := 'This is a new installation and no administrator username was given.' + #13#10#13#10 +
                'Run Setup again with /ADMINUSER=<name>.' + #13#10#13#10 +
                'Nothing has been installed.';
      Exit;
    end;

    if Length(AdminPage.Values[2]) < 10 then
    begin
      Result := 'This is a new installation and no administrator password of at least ' +
                '10 characters was given.' + #13#10#13#10 +
                'Run Setup again with /ADMINPASSWORD=<password>.' + #13#10#13#10 +
                'Nothing has been installed.';
      Exit;
    end;
  end;

  // Applies to every install, silent or not: the wizard checks this too,
  // but a silent one skipped straight past it.
  if StrToIntDef(Trim(PortPage.Values[0]), -1) < 1 then
  begin
    Result := 'The port must be a number between 1 and 65535. Got: ' +
              Trim(PortPage.Values[0]) + #13#10#13#10 + 'Nothing has been installed.';
    Exit;
  end;

  if Trim(PortPage.Values[0]) = GetDbPort('') then
  begin
    Result := 'The application and the bundled database were both given port ' +
              Trim(PortPage.Values[0]) + '. They need different ones.' + #13#10#13#10 +
              'Nothing has been installed.';
    Exit;
  end;

  // Without this, reinstalling over a working install fails: the running
  // service holds our own files. Inno's RestartManager prompt is no help
  // either, because a silent install answers it with Abort.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#PgServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Waited for, not slept through. Replacing a binary out from under a
  // running postgres.exe is how a data directory ends up needing recovery,
  // and the port checks below are meaningless until our own listeners are
  // gone.
  if not WaitForServicesStopped('90') then
  begin
    Result := 'Hospital PM is already installed on this computer and its services ' +
              'would not stop, so Setup cannot safely replace the files they are ' +
              'using.' + #13#10#13#10 +
              'Restart the computer and run Setup again.' + #13#10#13#10 +
              'Nothing has been installed.';
    Exit;
  end;

  AppPort := Trim(PortPage.Values[0]);
  DbPort := GetDbPort('');

  // Ports are checked AFTER stopping our own services, so on an upgrade we
  // are not reporting a conflict with ourselves.
  if PortInUse(AppPort) then
  begin
    Result := 'Another program on this computer is already using port ' + AppPort + '.' + #13#10#13#10 +
              'Run Setup again and choose a different port, or stop whatever is using it.' + #13#10#13#10 +
              'Nothing has been installed.';
    Exit;
  end;

  if PortInUse(DbPort) then
  begin
    Result := 'Port ' + DbPort + ' is already in use, and that is the port the bundled ' +
              'database needs.' + #13#10#13#10 +
              'This usually means another PostgreSQL is running on it. Run Setup again ' +
              'with /DBPORT= followed by a free port.' + #13#10#13#10 +
              'Nothing has been installed.';
    Exit;
  end;

  // An existing cluster whose credentials file is gone is a dead end: the
  // application's database password lives only in db.json and cannot be
  // recovered from the cluster. setup-database.ps1 detects this too, but by
  // then the payload is installed and its refusal cannot be reported.
  // Catching it here costs the hospital nothing and says what to do about it.
  if FileExists(DataDir + '\pgdata\PG_VERSION') and (not FileExists(DataDir + '\db.json')) then
  begin
    Result := 'This computer has a Hospital PM database from an earlier installation, ' +
              'but the file holding its password is missing:' + #13#10#13#10 +
              DataDir + '\db.json' + #13#10#13#10 +
              'Restore that file from a copy of this folder and run Setup again. ' +
              'Deleting the pgdata folder would let Setup start over, but that discards ' +
              'the existing database.' + #13#10#13#10 +
              'Nothing has been installed.';
    Exit;
  end;
end;

/// Validates a page the operator is looking at.
///
/// Returns immediately under /VERYSILENT, and that is the whole point.
/// Inno still calls this for pages it skips, and MsgBox is NOT suppressed
/// by /SUPPRESSMSGBOXES - that flag only governs Setup's own dialogs. So
/// an unattended install missing a parameter used to put a modal error box
/// on a machine with nobody at it, wait for a click that could not come,
/// then abort with exit code 1 and no install-failure.txt.
///
/// Worse, the application's own updater launches Setup exactly that way -
/// /VERYSILENT and no answers - so every in-app update stopped the service
/// and then hung on a dialog in session 0. Silent installs are validated in
/// PrepareToInstall instead, where a refusal is a returned string, a real
/// exit code, and no window.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  PortNumber: Integer;
begin
  Result := True;
  if WizardSilent then
    Exit;

  if CurPageID = HospitalPage.ID then
  begin
    if Trim(HospitalPage.Values[0]) = '' then
    begin
      MsgBox('Enter the hospital name. It appears on printed reports.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if CurPageID = AdminPage.ID then
  begin
    if Trim(AdminPage.Values[1]) = '' then
    begin
      MsgBox('Enter a username for the administrator.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // Matches the application's own rule. Discovering it after the install,
    // from a log, is not a discovery anyone should have to make.
    if Length(AdminPage.Values[2]) < 10 then
    begin
      MsgBox('The password must be at least 10 characters.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if AdminPage.Values[2] <> AdminPage.Values[3] then
    begin
      MsgBox('The two passwords do not match.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if CurPageID = PortPage.ID then
  begin
    PortNumber := StrToIntDef(Trim(PortPage.Values[0]), -1);
    if (PortNumber < 1) or (PortNumber > 65535) then
    begin
      MsgBox('The port must be a number between 1 and 65535. 5000 is a sensible default.',
             mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if PortNumber = StrToIntDef(GetDbPort(''), 5433) then
    begin
      MsgBox('That is the port the bundled database uses. Choose a different one.',
             mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

/// Escapes a value for embedding in JSON.
function JsonEscape(Value: String): String;
begin
  StringChangeEx(Value, '\', '\\', True);
  StringChangeEx(Value, '"', '\"', True);
  Result := Value;
end;



/// Records the first failure, writes it down, and shows it.
///
/// The message box is suppressible so an unattended install does not hang on
/// it - which means on those installs the file is the only record, hence
/// writing the file first and unconditionally.
procedure Fail(const Reason, Detail: String);
var
  Lines: TStringList;
begin
  // Only the first failure is kept. The ones after it are its consequences.
  if FailureReason <> '' then
    Exit;

  FailureReason := Reason;
  Log('INSTALL FAILED: ' + Reason);

  Lines := TStringList.Create;
  try
    Lines.Add('Hospital PM {#AppVersion} did not finish installing.');
    Lines.Add('');
    Lines.Add(GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':'));
    Lines.Add('');
    Lines.Add('What went wrong');
    Lines.Add('---------------');
    Lines.Add(Reason);
    if Detail <> '' then
    begin
      Lines.Add('');
      Lines.Add(Detail);
    end;
    Lines.Add('');
    Lines.Add('Where to look');
    Lines.Add('-------------');
    Lines.Add('Database setup log: ' + DataDir + '\setup-database.log');
    Lines.Add('PostgreSQL log:     ' + DataDir + '\pgdata\log');
    Lines.Add('Service errors:     Event Viewer, Windows Logs, Application,');
    Lines.Add('                    source HospitalPM');
    Lines.Add('');
    // Said here because otherwise the next thing that happens to someone
    // following these paths is an access-denied box, and it reads like a
    // second fault rather than the intended one.
    Lines.Add('The two log files are in a folder restricted to administrators, so');
    Lines.Add('opening them will ask you to confirm. That is deliberate: if a database');
    Lines.Add('command fails, its log can contain the generated database password.');
    Lines.Add('');
    Lines.Add('None of these hold patient data, but treat the two log files as');
    Lines.Add('sensitive - read them before sending them on.');

    Lines.SaveToFile(FailureFile);
  finally
    Lines.Free;
  end;

  SuppressibleMsgBox(
    'Hospital PM was installed but could not be started.' + #13#10#13#10 +
    Reason + #13#10#13#10 +
    'The details are in:' + #13#10 + FailureFile,
    mbCriticalError, MB_OK, IDOK);
end;

procedure Status(const Message: String);
begin
  Log('STEP: ' + Message);
  if WizardForm <> nil then
  begin
    WizardForm.StatusLabel.Caption := Message;
    WizardForm.Refresh;
  end;
end;

/// Writes this machine's settings into the locked data directory.
///
/// Returns False rather than raising. An exception here is swallowed by Inno
/// and the install carries on regardless, which is the bug this whole
/// restructuring exists to fix - so failure has to be a return value that the
/// caller checks.
function WriteSettings: Boolean;
var
  Settings: TStringList;
  CredentialsPath: String;
  // AnsiString, because that is what LoadStringFromFile takes. The file is
  // machine-written ASCII, so nothing is lost.
  Credentials: AnsiString;
  DbHost, DbPort, DbName, DbUser, DbPassword: String;
  ConnectionString: String;
begin
  Result := False;
  ForceDirectories(DataDir);

  CredentialsPath := DataDir + '\db.json';

  if not FileExists(CredentialsPath) then
  begin
    Fail('The database setup wrote no credentials file, so there is nothing to ' +
         'configure the application with.',
         'Expected: ' + CredentialsPath);
    Exit;
  end;

  if not LoadStringFromFile(CredentialsPath, Credentials) then
  begin
    Fail('The database credentials could not be read.', 'File: ' + CredentialsPath);
    Exit;
  end;

  DbHost := ReadJsonField(String(Credentials), 'host');
  DbPort := ReadJsonField(String(Credentials), 'port');
  DbName := ReadJsonField(String(Credentials), 'database');
  DbUser := ReadJsonField(String(Credentials), 'username');
  DbPassword := ReadJsonField(String(Credentials), 'password');

  if (DbHost = '') or (DbPort = '') or (DbName = '') or (DbUser = '') or (DbPassword = '') then
  begin
    Fail('The database credentials are incomplete, so the application cannot be told ' +
         'how to reach its database.',
         'File: ' + CredentialsPath);
    Exit;
  end;

  ConnectionString :=
    'Host=' + JsonEscape(DbHost) +
    ';Port=' + JsonEscape(DbPort) +
    ';Database=' + JsonEscape(DbName) +
    ';Username=' + JsonEscape(DbUser) +
    ';Password=' + JsonEscape(DbPassword);

  // Read before the file goes, and only used when this run was not told a
  // name. An upgrade started by the in-app updater passes no /HOSPITAL,
  // because it has none to pass; without this it would blank the name on
  // every printed report the hospital produces afterwards.
  if Trim(HospitalPage.Values[0]) = '' then
    HospitalPage.Values[0] := ExistingHospitalName();

  // Deleted rather than overwritten. A file created fresh inside the locked
  // folder inherits the correct ACEs; an existing one keeps whatever it had,
  // which on a machine upgraded from a broken build is nothing at all.
  if FileExists(DataDir + '\appsettings.json') then
    DeleteFile(DataDir + '\appsettings.json');

  // A licence the operator selected. Copied rather than validated here: the
  // application checks the signature when it reads it, and a bad file should
  // surface on the Licence page rather than stop an install.
  if (Trim(LicencePage.Values[0]) <> '') and FileExists(Trim(LicencePage.Values[0])) then
    CopyFile(Trim(LicencePage.Values[0]), DataDir + '\hospitalpm.licence', False);

  Settings := TStringList.Create;
  try
    Settings.Add('{');
    Settings.Add('  "ConnectionStrings": {');
    Settings.Add('    "HospitalPm": "' + ConnectionString + '"');
    Settings.Add('  },');
    // "+" and not "0.0.0.0". Kestrel binds 0.0.0.0 to IPv4 ONLY, while
    // Windows resolves "localhost" to ::1 first - and the Start Menu shortcut
    // this installer creates points at http://localhost:<port>/.
    //
    // So every request from the shortcut tried IPv6, found nothing listening,
    // and fell back to IPv4. Measured on a real install: 210 ms to connect
    // via localhost against 0.8 ms via 127.0.0.1, on every new connection.
    // The request itself took 4 ms; the wait was entirely the fallback.
    //
    // "+" binds both stacks - netstat then shows 0.0.0.0 AND [::] - which is
    // what a hospital's browser, and every phone on the ward network, needs.
    // Kestrel needs no URL ACL for this; that is an HTTP.sys requirement and
    // this does not use HTTP.sys.
    Settings.Add('  "Urls": "http://+:' + Trim(PortPage.Values[0]) + '",');
    // Applied on the first start and then removed from this file, so a
    // hospital finishes the installer with a working login instead of a web
    // page asking them to invent one.
    Settings.Add('  "FirstRun": {');
    Settings.Add('    "HospitalName": "' + JsonEscape(Trim(HospitalPage.Values[0])) + '",');
    Settings.Add('    "AdminUserName": "' + JsonEscape(Trim(AdminPage.Values[1])) + '",');
    Settings.Add('    "AdminFullName": "' + JsonEscape(Trim(AdminPage.Values[0])) + '",');
    Settings.Add('    "AdminPassword": "' + JsonEscape(AdminPage.Values[2]) + '"');
    Settings.Add('  },');
    Settings.Add('  "Backup": {');
    Settings.Add('    "Directory": "' + JsonEscape(DataDir + '\backups') + '",');
    // The bundled pg_dump, so backups never depend on what else is installed
    // or on which PostgreSQL happens to be first on PATH.
    Settings.Add('    "PgDumpPath": "' + JsonEscape(ExpandConstant('{app}\pgsql\bin\pg_dump.exe')) + '",');
    Settings.Add('    "PgRestorePath": "' + JsonEscape(ExpandConstant('{app}\pgsql\bin\pg_restore.exe')) + '"');
    Settings.Add('  },');
    // Where someone drops an update that arrived on a USB stick, and where a
    // verified installer is copied to before it is run. Under DataDir rather
    // than {app}: a staged installer is a couple of hundred megabytes, Program
    // Files is readable by every local user, and an uninstall removes {app}.
    Settings.Add('  "Update": {');
    Settings.Add('    "Directory": "' + JsonEscape(DataDir + '\updates') + '",');
    Settings.Add('    "StagingDirectory": "' + JsonEscape(DataDir + '\updates\staging') + '"');
    Settings.Add('  }');
    Settings.Add('}');
    Settings.SaveToFile(DataDir + '\appsettings.json');
  finally
    Settings.Free;
  end;

  Result := True;
end;

/// Waits until the application answers on /health, or gives up.
///
/// This is the only step that checks the thing the hospital actually cares
/// about. "sc start returned 0" means the service was asked to start; it says
/// nothing about whether migrations ran, whether the database was reachable,
/// or whether the process died two seconds later. All three have happened
/// during development, and each one looked like a clean install.
///
/// Two minutes: a delayed-auto service on a slow hospital PC, plus the first
/// migration run against an empty database.
function WaitForHealth(const Port: String): Boolean;
begin
  Result := RunPowerShell(
    '$deadline = (Get-Date).AddSeconds(120); ' +
    'do { ' +
    '  try { ' +
    '    $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 ' +
    '           -Uri ''http://localhost:' + Port + '/health''; ' +
    '    if ($r.StatusCode -eq 200) { exit 0 } ' +
    '  } catch { } ' +
    '  Start-Sleep -Seconds 3 ' +
    '} while ((Get-Date) -lt $deadline); ' +
    'exit 1') = 0;
end;

/// Everything that used to live in [Run], in order, with every exit code
/// checked and the first failure recorded.
procedure InstallSteps;
var
  Code: Integer;
  Port, DbPort: String;
begin
  Port := Trim(PortPage.Values[0]);
  DbPort := GetDbPort('');

  // A note left by a previous failed attempt must not be read as describing
  // this one.
  if FileExists(FailureFile) then
    DeleteFile(FailureFile);

  // --- 1. Lock the data folder down, BEFORE anything is written into it ----
  // It holds the database password, and ProgramData is world-readable by
  // default, so inheritance is broken and Users removed.
  //
  // The order matters and is not obvious. Doing this after writing the
  // settings file, with /T, strips that file's inherited ACEs while the
  // (OI)(CI) grants - container inheritance flags - apply nothing to a file.
  // The result is a file with an empty ACL that not even LocalSystem can
  // read: the service installs, fails to start, and says why only in the
  // Event Log. Locking the folder first lets the file inherit the right ACEs
  // when it is created.
  //
  // No /T, for the same reason: on an upgrade it would blank the existing
  // file.
  Status('Securing the settings folder...');
  ForceDirectories(DataDir);
  if not RunChecked(ExpandConstant('{sys}\icacls.exe'),
                    '"' + DataDir + '" /inheritance:r ' +
                    '/grant "*S-1-5-18:(OI)(CI)F" /grant "*S-1-5-32-544:(OI)(CI)F" /C',
                    Code) then
  begin
    Fail('The folder holding the database password could not be secured, so Setup ' +
         'stopped rather than leave it readable by every user of this computer.',
         'icacls exited with code ' + IntToStr(Code) + ' for ' + DataDir);
    Exit;
  end;

  // --- 2. Antivirus exclusion for the database directory -------------------
  // Real-time scanning of a PostgreSQL data directory is a well-known cause
  // of corruption and of write stalls that look like the application hanging.
  // Deliberately scoped to the cluster only, never the whole install.
  //
  // Best effort, and deliberately NOT checked: Defender may be absent,
  // replaced by another product, or centrally managed, and none of those
  // should fail an install. Note that this therefore does nothing at all on a
  // machine running third-party antivirus, where the exclusion has to be
  // added by hand. Called out in docs/REVIEW.md.
  Status('Configuring antivirus exclusion...');
  RunPowerShell('try { Add-MpPreference -ExclusionPath ''' + DataDir +
                '\pgdata'' -ErrorAction Stop } catch { }');

  // --- 3. Create the bundled database --------------------------------------
  // initdb, register the PostgreSQL service, generate credentials, create the
  // role and database. The hospital is never asked for any of it.
  //
  // The script is idempotent: reinstalling over an existing cluster keeps it,
  // because that cluster holds the equipment register.
  Status('Setting up the database (this takes a minute)...');
  if not RunChecked(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
                    '-NoProfile -ExecutionPolicy Bypass -File "' +
                    ExpandConstant('{app}\setup-database.ps1') + '"' +
                    ' -PgRoot "' + ExpandConstant('{app}\pgsql') + '"' +
                    ' -DataDir "' + DataDir + '\pgdata"' +
                    ' -OutFile "' + DataDir + '\db.json"' +
                    ' -Port ' + DbPort +
                    ' -ServiceName {#PgServiceName}' +
                    ' -LogFile "' + DataDir + '\setup-database.log"',
                    Code) then
  begin
    Fail('The database could not be set up, so Hospital PM has nowhere to store ' +
         'records.',
         'setup-database.ps1 exited with code ' + IntToStr(Code) + '. The last lines ' +
         'of setup-database.log say which step failed.');
    Exit;
  end;

  // --- 4. Settings ---------------------------------------------------------
  // Written now and not earlier, because the database password is generated
  // by the step above. They land inside the already-locked folder and inherit
  // its permissions.
  Status('Writing settings...');
  if not WriteSettings then
    Exit;

  // --- 5. Register the application service ---------------------------------
  // Delayed start: PostgreSQL must be accepting connections before we migrate
  // against it, and on a slow hospital PC that is not instant.
  Status('Registering the service...');
  if not RunChecked(ExpandConstant('{sys}\sc.exe'),
                    'create {#ServiceName} binPath= "' + ExpandConstant('{app}\{#ExeName}') +
                    '" DisplayName= "{#ServiceDisplay}" start= delayed-auto', Code) then
  begin
    // 1073 is ERROR_SERVICE_EXISTS, which is the normal case on a reinstall
    // and not a problem: the binary path has not changed.
    if Code <> 1073 then
    begin
      Fail('The Hospital PM service could not be registered with Windows.',
           'sc create exited with code ' + IntToStr(Code) + '.');
      Exit;
    end;
    Log('Service already registered - this is a reinstall, continuing.');
  end;

  RunChecked(ExpandConstant('{sys}\sc.exe'),
             'description {#ServiceName} "Biomedical equipment preventive maintenance. ' +
             'Serves the Hospital PM web application."', Code);

  // If the service dies, restart it. A ward should not lose the system
  // because of one bad night, and nobody there will be watching services.msc.
  RunChecked(ExpandConstant('{sys}\sc.exe'),
             'failure {#ServiceName} reset= 86400 ' +
             'actions= restart/60000/restart/60000/restart/120000', Code);

  // --- 6. Firewall ---------------------------------------------------------
  // Phones and other PCs on the ward network need to reach this machine.
  // Scoped to private networks: a hospital PC that ends up on a public Wi-Fi
  // should not be serving its asset register to it.
  //
  // Not fatal. A machine that cannot open its firewall still works perfectly
  // for the person sitting at it, and refusing the whole install over LAN
  // access would be the wrong trade. Deleted first so a reinstall on a
  // different port does not leave the old rule behind.
  Status('Opening the firewall...');
  RunChecked(ExpandConstant('{sys}\netsh.exe'),
             'advfirewall firewall delete rule name="{#AppName}"', Code);
  if not RunChecked(ExpandConstant('{sys}\netsh.exe'),
                    'advfirewall firewall add rule name="{#AppName}" dir=in action=allow ' +
                    'protocol=TCP localport=' + Port + ' profile=private,domain', Code) then
    Log('WARNING: the firewall rule could not be added (netsh exited ' +
        IntToStr(Code) + '). Hospital PM will work on this computer but may not be ' +
        'reachable from other machines.');

  // --- 7. Start ------------------------------------------------------------
  Status('Starting the service...');
  if not RunChecked(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}', Code) then
  begin
    Fail('The Hospital PM service would not start.',
         'sc start exited with code ' + IntToStr(Code) + '.');
    Exit;
  end;

  // --- 8. Prove it actually works ------------------------------------------
  Status('Waiting for Hospital PM to answer...');
  if not WaitForHealth(Port) then
  begin
    Fail('Hospital PM was installed and started but never answered on port ' + Port +
         ', so it is not usable yet.',
         'No reply from http://localhost:' + Port + '/health within two minutes. ' +
         'The service is usually still running; the Event Log says why it is not ' +
         'serving.');
    Exit;
  end;

  Status('Hospital PM is running.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallSteps;
end;

/// The Ready page, which is the last thing anyone reads before committing.
/// It names where a failure would be reported, because that is the one piece
/// of information nobody has when they need it.
function UpdateReadyMemo(const Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine +
            'Hospital:' + NewLine +
            Space + Trim(HospitalPage.Values[0]) + NewLine + NewLine +
            'Administrator:' + NewLine +
            Space + Trim(AdminPage.Values[1]) + NewLine + NewLine +
            'Reachable at:' + NewLine +
            Space + 'http://localhost:' + Trim(PortPage.Values[0]) + '/' + NewLine + NewLine +
            'Database:' + NewLine +
            Space + 'bundled PostgreSQL, port ' + GetDbPort('') + NewLine + NewLine +
            'If anything goes wrong, Setup writes the reason to:' + NewLine +
            Space + FailureFile;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
  RemoveData: Boolean;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // install-failure.txt is written at run time by Fail(), so Inno has no
    // record of it and will not remove it - and one unknown file is enough to
    // leave the whole install directory behind, on exactly the machines where
    // an install went wrong.
    //
    // Deleted HERE rather than through [UninstallDelete]. Those entries are
    // processed after Inno has already tried to remove the directory, so the
    // file goes but an empty "Hospital PM" folder stays in Program Files.
    // Measured, after an uninstall test found the folder still there.
    DeleteFile(ExpandConstant('{app}\install-failure.txt'));

    // Both services stopped here, before [UninstallRun] and before any file
    // is deleted.
    //
    // sc stop returns as soon as it has signalled the service, not when the
    // process has exited. Without the wait below, Inno starts deleting while
    // hospitalpm.exe is still running and leaves the binary behind - an
    // uninstall that appears to succeed and does not.
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#PgServiceName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Long enough for an ASP.NET Core host to drain and for pg_ctl to shut
    // the cluster down cleanly. Deleting a service out from under a running
    // postgres.exe is how a data directory ends up needing recovery.
    Sleep(10000);

    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#PgServiceName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Inno will not remove the install directory once a file it has no record
    // of has lived there - install-failure.txt, written at run time by Fail().
    // Deleting that file early (above) is not enough on its own; the directory
    // is still left behind, empty. Measured, twice.
    //
    // RemoveDir has exactly the right semantics: it removes the directory only
    // if it is empty, so anything a hospital put there by hand survives.
    RemoveDir(ExpandConstant('{app}'));

    Dir := ExpandConstant('{commonappdata}\{#AppName}');

    // Data is kept unless removal is asked for explicitly. Someone
    // uninstalling to fix a problem is not asking to lose the equipment
    // register, and this is the one mistake in an uninstaller that cannot be
    // undone.
    //
    // Deletion is opt-in through /REMOVEDATA=1 rather than a prompt, because
    // an unattended uninstall never answers a prompt. An earlier version
    // asked with MsgBox and a No default, and a /VERYSILENT uninstall deleted
    // the folder anyway - which on a real install is the hospital's entire
    // database. A destructive default that only shows up when nobody is
    // watching is the worst kind.
    if DirExists(Dir) then
    begin
      RemoveData := ExpandConstant('{param:REMOVEDATA|0}') = '1';

      if (not RemoveData) and (not UninstallSilent) then
      begin
        RemoveData := SuppressibleMsgBox(
          'Remove Hospital PM''s data as well?' + #13#10#13#10 +
          'This deletes the database itself, every backup, the settings and' + #13#10 +
          'the licence, in:' + #13#10 +
          Dir + #13#10#13#10 +
          'There is no way to undo this.' + #13#10#13#10 +
          'Choose No to keep them.',
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
      end;

      if RemoveData then
      begin
        // The antivirus exclusion refers to a directory that is about to stop
        // existing; leaving it behind is untidy at best and misleading later.
        Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
             '-NoProfile -ExecutionPolicy Bypass -Command "try { Remove-MpPreference ' +
             '-ExclusionPath ''' + Dir + '\pgdata'' -ErrorAction Stop } catch { }"',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

        DelTree(Dir, True, True, True);
      end;
    end;
  end;
end;
