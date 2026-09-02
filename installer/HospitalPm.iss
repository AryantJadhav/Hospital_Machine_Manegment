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
; --- 1. Lock the data folder down, BEFORE anything is written into it ------
; It holds the database password, and ProgramData is world-readable by
; default, so inheritance is broken and Users removed.
;
; The order matters and is not obvious. Doing this after writing the settings
; file, with /T, strips that file's inherited ACEs while the (OI)(CI) grants -
; container inheritance flags - apply nothing to a file. The result is a file
; with an empty ACL that not even LocalSystem can read: the service installs,
; fails to start, and says why only in the Event Log. Locking the folder first
; lets the file inherit the right ACEs when it is created.
;
; No /T, for the same reason: on an upgrade it would blank the existing file.
Filename: "{sys}\icacls.exe"; \
  Parameters: """{commonappdata}\{#AppName}"" /inheritance:r /grant ""*S-1-5-18:(OI)(CI)F"" /grant ""*S-1-5-32-544:(OI)(CI)F"" /C"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Securing the settings folder..."

; --- 2. Antivirus exclusion for the database directory --------------------
; Real-time scanning of a PostgreSQL data directory is a well-known cause of
; corruption and of write stalls that look like the application hanging.
; Deliberately scoped to the cluster only, never the whole install.
;
; Best effort: Defender may be absent, replaced, or centrally managed, and
; none of those should fail an install. runhidden so a hospital never sees a
; console window it will worry about.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""try {{ Add-MpPreference -ExclusionPath '{commonappdata}\{#AppName}\pgdata' -ErrorAction Stop }} catch {{ }}"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Configuring antivirus exclusion..."

; --- 3. Create the bundled database ---------------------------------------
; initdb, register the PostgreSQL service, generate credentials, create the
; role and database. The hospital is never asked for any of it.
;
; The script is idempotent: reinstalling over an existing cluster keeps it,
; because that cluster holds the equipment register.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\setup-database.ps1"" -PgRoot ""{app}\pgsql"" -DataDir ""{commonappdata}\{#AppName}\pgdata"" -OutFile ""{commonappdata}\{#AppName}\db.json"" -Port {code:GetDbPort} -ServiceName {#PgServiceName}"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Setting up the database (this takes a minute)...";

; --- 4. Register the application service -----------------------------------
; Settings are written immediately before this, so they land inside the
; already-locked folder and inherit its permissions. They can only be written
; now, because the database password is generated by the step above.
;
; Delayed start: PostgreSQL must be accepting connections before we migrate
; against it, and on a slow hospital PC that is not instant.
Filename: "{sys}\sc.exe"; \
  Parameters: "create {#ServiceName} binPath= ""{app}\{#ExeName}"" DisplayName= ""{#ServiceDisplay}"" start= delayed-auto"; \
  Flags: runhidden waituntilterminated; BeforeInstall: WriteSettings; \
  StatusMsg: "Registering the service..."

Filename: "{sys}\sc.exe"; \
  Parameters: "description {#ServiceName} ""Biomedical equipment preventive maintenance. Serves the Hospital PM web application."""; \
  Flags: runhidden waituntilterminated

; If the service dies, restart it. A ward should not lose the system because
; of one bad night, and nobody there will be watching services.msc.
Filename: "{sys}\sc.exe"; \
  Parameters: "failure {#ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/120000"; \
  Flags: runhidden waituntilterminated

; --- 4. Firewall ----------------------------------------------------------
; Phones and other PCs on the ward network need to reach this machine. Scoped
; to private networks: a hospital PC that ends up on a public Wi-Fi should not
; be serving its asset register to it.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow protocol=TCP localport={code:GetPort} profile=private,domain"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Opening the firewall..."

; --- 5. Start --------------------------------------------------------------
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Starting the service..."

Filename: "http://localhost:{code:GetPort}/"; Flags: shellexec postinstall nowait; \
  Description: "Open {#AppName} now"

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
  PortPage.Values[0] := ExpandConstant('{param:PORT|5000}');
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

/// Stops both services before any file is replaced.
///
/// Without this, reinstalling over a working install fails: RestartManager
/// finds the running service holding our files, cannot shut it down, and
/// Setup aborts - which is exactly what a hospital does when it upgrades or
/// reinstalls after a support call. Inno's own RestartManager prompt is no
/// help either, because a silent install answers it with Abort.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#PgServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Both need a moment to actually exit. Replacing a binary out from under a
  // running postgres.exe is how a data directory ends up needing recovery.
  Sleep(6000);
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
  Result := ExpandConstant('{param:DBPORT|5433}');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PortNumber: Integer;
begin
  Result := True;

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

procedure WriteSettings;
var
  Settings: TStringList;
  DataDir, CredentialsPath: String;
  // AnsiString, because that is what LoadStringFromFile takes. The file is
  // machine-written ASCII, so nothing is lost.
  Credentials: AnsiString;
  DbHost, DbPort, DbName, DbUser, DbPassword: String;
  ConnectionString: String;
begin
  DataDir := ExpandConstant('{commonappdata}\{#AppName}');
  ForceDirectories(DataDir);

  CredentialsPath := DataDir + '\db.json';

  // The database step runs before this one. If its output is missing, that
  // step failed, and continuing would register a service that cannot start
  // and leave an administrator guessing. Say so instead.
  if not FileExists(CredentialsPath) then
    RaiseException(
      'The database was not set up, so there are no credentials to configure.' + #13#10 +
      'Look in ' + ExpandConstant('{commonappdata}\{#AppName}\pgdata\log') +
      ' for what PostgreSQL reported.');

  if not LoadStringFromFile(CredentialsPath, Credentials) then
    RaiseException('The database credentials at ' + CredentialsPath + ' could not be read.');

  DbHost := ReadJsonField(String(Credentials), 'host');
  DbPort := ReadJsonField(String(Credentials), 'port');
  DbName := ReadJsonField(String(Credentials), 'database');
  DbUser := ReadJsonField(String(Credentials), 'username');
  DbPassword := ReadJsonField(String(Credentials), 'password');

  if (DbHost = '') or (DbPort = '') or (DbName = '') or (DbUser = '') or (DbPassword = '') then
    RaiseException('The database credentials at ' + CredentialsPath + ' are incomplete.');

  ConnectionString :=
    'Host=' + JsonEscape(DbHost) +
    ';Port=' + JsonEscape(DbPort) +
    ';Database=' + JsonEscape(DbName) +
    ';Username=' + JsonEscape(DbUser) +
    ';Password=' + JsonEscape(DbPassword);

  // Deleted rather than overwritten. A file created fresh inside the locked
  // folder inherits the correct ACEs; an existing one keeps whatever it had,
  // which on a machine upgraded from a broken build is nothing at all.
  if FileExists(DataDir + '\appsettings.json') then
    DeleteFile(DataDir + '\appsettings.json');

  // A licence the operator selected. Copied rather than validated here: the
  // application checks the signature when it reads it, and a bad file should
  // surface on the Licence page rather than stop an install.
  if (Trim(LicencePage.Values[0]) <> '') and FileExists(Trim(LicencePage.Values[0])) then
  begin
    CopyFile(Trim(LicencePage.Values[0]), DataDir + '\hospitalpm.licence', False);
  end;

  Settings := TStringList.Create;
  try
    Settings.Add('{');
    Settings.Add('  "ConnectionStrings": {');
    Settings.Add('    "HospitalPm": "' + ConnectionString + '"');
    Settings.Add('  },');
    Settings.Add('  "Urls": "http://0.0.0.0:' + Trim(PortPage.Values[0]) + '",');
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
    Settings.Add('  }');
    Settings.Add('}');
    Settings.SaveToFile(DataDir + '\appsettings.json');
  finally
    Settings.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  RemoveData: Boolean;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
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
    DataDir := ExpandConstant('{commonappdata}\{#AppName}');

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
    if DirExists(DataDir) then
    begin
      RemoveData := ExpandConstant('{param:REMOVEDATA|0}') = '1';

      if (not RemoveData) and (not UninstallSilent) then
      begin
        RemoveData := SuppressibleMsgBox(
          'Remove Hospital PM''s data as well?' + #13#10#13#10 +
          'This deletes the database itself, every backup, the settings and' + #13#10 +
          'the licence, in:' + #13#10 +
          DataDir + #13#10#13#10 +
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
             '-ExclusionPath ''' + DataDir + '\pgdata'' -ErrorAction Stop } catch { }"',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
