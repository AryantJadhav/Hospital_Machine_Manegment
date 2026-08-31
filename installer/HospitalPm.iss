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
var
  PortPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  // One question, because there is only one the hospital can usefully answer.
  // The database is created by the installer, on a port nobody needs to know,
  // with credentials nobody needs to see - so there is nothing to ask about it.
  PortPage := CreateInputQueryPage(wpSelectDir,
    'Network', 'Which port should Hospital PM use?',
    'Staff will reach the system at http://<this computer>:<port>/ from a browser or a phone.' + #13#10 +
    'The default suits almost every installation.');
  PortPage.Add('Port', False);

  // Defaults come from the command line so an unattended install works.
  // /VERYSILENT skips this page, which means whatever is set here is what
  // gets used - so this is also how SCCM and Intune drive it:
  //
  //   HospitalPM-Setup.exe /VERYSILENT /PORT=5000 /DBPORT=5433
  PortPage.Values[0] := ExpandConstant('{param:PORT|5000}');
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

  Settings := TStringList.Create;
  try
    Settings.Add('{');
    Settings.Add('  "ConnectionStrings": {');
    Settings.Add('    "HospitalPm": "' + ConnectionString + '"');
    Settings.Add('  },');
    Settings.Add('  "Urls": "http://0.0.0.0:' + Trim(PortPage.Values[0]) + '",');
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
    // The bundled PostgreSQL service, stopped and unregistered before its
    // files go. Left registered, it would point at a directory that no longer
    // holds an executable and fail noisily on every boot.
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#PgServiceName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // pg_ctl needs a moment to shut the cluster down cleanly; deleting the
    // service out from under a running postgres.exe is how a data directory
    // gets left needing recovery.
    Sleep(5000);

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
