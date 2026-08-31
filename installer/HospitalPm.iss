; Hospital PM — Windows installer
;
; Target: a hospital PC with no IT staff. Every decision here assumes the
; person running it will not read documentation, cannot be asked to edit a
; config file, and will not know what a service is.
;
; This installs the application and registers it as a Windows Service. It does
; not yet bundle PostgreSQL — the wizard asks for an existing server. Bundling
; is the next slice; doing both at once would make neither testable.
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

[Dirs]
; Data lives outside the install directory so uninstalling the program cannot
; delete the hospital's backups, licence or configuration.
Name: "{commonappdata}\{#AppName}"
Name: "{commonappdata}\{#AppName}\backups"
; The JWT signing key. It belongs inside the locked folder rather than beside
; the binary: anyone who can read it can forge a token for any user, and
; Program Files grants BUILTIN\Users read by default.
Name: "{commonappdata}\{#AppName}\keys"

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

; --- 2. Register the service ----------------------------------------------
; Settings are written immediately before this, so they land inside the
; already-locked folder and inherit its permissions.
;
; Delayed start: PostgreSQL must be accepting connections before we try to
; migrate against it, and on a slow hospital PC that is not instant.
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
  DbPage: TInputQueryWizardPage;
  PortPage: TInputQueryWizardPage;
  KeepDataPage: TInputOptionWizardPage;

procedure InitializeWizard;
begin
  DbPage := CreateInputQueryPage(wpSelectDir,
    'Database', 'Where is PostgreSQL?',
    'Hospital PM stores its records in PostgreSQL. Enter the details for the server on this machine.' + #13#10 +
    'If you do not know these, whoever installed PostgreSQL will.');
  DbPage.Add('Host', False);
  DbPage.Add('Port', False);
  DbPage.Add('Database name', False);
  DbPage.Add('Username', False);
  DbPage.Add('Password', True);
  // Defaults come from the command line so an unattended install works.
  // /VERYSILENT skips these pages entirely, which means whatever is set here
  // is what gets written - so this is also how SCCM and Intune drive it:
  //
  //   HospitalPM-Setup.exe /VERYSILENT /DBHOST=localhost /DBNAME=hospitalpm
  //     /DBUSER=postgres /DBPASSWORD=secret /PORT=5000
  DbPage.Values[0] := ExpandConstant('{param:DBHOST|localhost}');
  DbPage.Values[1] := ExpandConstant('{param:DBPORT|5432}');
  DbPage.Values[2] := ExpandConstant('{param:DBNAME|hospitalpm}');
  DbPage.Values[3] := ExpandConstant('{param:DBUSER|postgres}');
  DbPage.Values[4] := ExpandConstant('{param:DBPASSWORD|}');

  PortPage := CreateInputQueryPage(DbPage.ID,
    'Network', 'Which port should Hospital PM use?',
    'Staff will reach the system at http://<this computer>:<port>/ from a browser or a phone.');
  PortPage.Add('Port', False);
  PortPage.Values[0] := ExpandConstant('{param:PORT|5000}');
end;

function GetPort(Param: String): String;
begin
  Result := PortPage.Values[0];
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PortNumber: Integer;
begin
  Result := True;

  if CurPageID = DbPage.ID then
  begin
    if Trim(DbPage.Values[0]) = '' then
    begin
      MsgBox('Enter the database host. On a single-machine install this is localhost.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(DbPage.Values[2]) = '' then
    begin
      MsgBox('Enter the database name.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(DbPage.Values[4]) = '' then
    begin
      // Refusing an empty password here is not pedantry: a blank one usually
      // means the field was missed, and the service would then fail to start
      // with an error nobody on site can read.
      MsgBox('Enter the database password.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if CurPageID = PortPage.ID then
  begin
    PortNumber := StrToIntDef(Trim(PortPage.Values[0]), -1);
    if (PortNumber < 1) or (PortNumber > 65535) then
    begin
      MsgBox('The port must be a number between 1 and 65535. 5000 is a sensible default.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

/// Escapes a value for embedding in JSON. Hospital passwords contain
/// backslashes and quotes more often than anyone expects.
function JsonEscape(Value: String): String;
begin
  StringChangeEx(Value, '\', '\\', True);
  StringChangeEx(Value, '"', '\"', True);
  Result := Value;
end;

procedure WriteSettings;
var
  Settings: TStringList;
  DataDir: String;
  ConnectionString: String;
begin
  // Validated here as well as in the wizard: a silent install never shows the
  // pages, so NextButtonClick never runs. Failing loudly beats registering a
  // service that cannot start and leaving an administrator to guess why.
  if Trim(DbPage.Values[4]) = '' then
    RaiseException('No database password was supplied. For an unattended install pass /DBPASSWORD=...');
  if Trim(DbPage.Values[2]) = '' then
    RaiseException('No database name was supplied. For an unattended install pass /DBNAME=...');
  if StrToIntDef(Trim(PortPage.Values[0]), -1) < 1 then
    RaiseException('The port must be a number between 1 and 65535. Pass /PORT=...');

  DataDir := ExpandConstant('{commonappdata}\{#AppName}');
  ForceDirectories(DataDir);

  ConnectionString :=
    'Host=' + JsonEscape(Trim(DbPage.Values[0])) +
    ';Port=' + JsonEscape(Trim(DbPage.Values[1])) +
    ';Database=' + JsonEscape(Trim(DbPage.Values[2])) +
    ';Username=' + JsonEscape(Trim(DbPage.Values[3])) +
    ';Password=' + JsonEscape(DbPage.Values[4]);

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
    Settings.Add('    "Directory": "' + JsonEscape(DataDir + '\backups') + '"');
    Settings.Add('  }');
    Settings.Add('}');
    Settings.SaveToFile(DataDir + '\appsettings.json');
  finally
    Settings.Free;
  end;
end;

procedure InitializeUninstallProgressForm;
begin
  KeepDataPage := nil;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  RemoveData: Boolean;
begin
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
    // the folder anyway - which on a real install is the hospital's backups
    // and its licence. A destructive default that only shows up when nobody
    // is watching is the worst kind.
    if DirExists(DataDir) then
    begin
      RemoveData := ExpandConstant('{param:REMOVEDATA|0}') = '1';

      if (not RemoveData) and (not UninstallSilent) then
      begin
        RemoveData := SuppressibleMsgBox(
          'Remove Hospital PM''s data as well?' + #13#10#13#10 +
          'This deletes the settings, the licence and every database backup in:' + #13#10 +
          DataDir + #13#10#13#10 +
          'The PostgreSQL database itself is not touched.' + #13#10#13#10 +
          'Choose No to keep them.',
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
      end;

      if RemoveData then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
