; Installer for AudioMonitor / AudioFunbox (Inno Setup 6).
; Build from the project root, after publishing both exes into "portable versions":
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=x.y.z installer\AudioMonitorFunbox.iss
;
; First run: pick AudioMonitor (simple) or AudioFunbox (full, with the Pedalboard).
; Run again with AudioMonitor installed:  Upgrade to the full version / Uninstall / Update AudioMonitor.
; Run again with AudioFunbox installed:   Remove pedalboard / Uninstall / Update AudioFunbox.
; Installs for the current Windows user only (no administrator rights). Silent use: /SILENT or /VERYSILENT,
; with /EDITION=Monitor or /EDITION=Funbox to choose (default: keep what's installed, else Monitor).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6E2C7B1D-4A9F-4C3E-9B58-1F0D7A3E52C4}
AppName=AudioMonitor Funbox
AppVersion={#AppVersion}
AppVerName=AudioMonitor Funbox {#AppVersion}
AppPublisher=munchiefer
AppPublisherURL=https://github.com/munchiefer/AudioMonitorFunbox
AppSupportURL=https://github.com/munchiefer/AudioMonitorFunbox/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\AudioMonitorFunbox
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableWelcomePage=yes
WizardStyle=modern
SetupIconFile=..\app.ico
UninstallDisplayName=AudioMonitor Funbox
UninstallDisplayIcon={app}\{code:ExeName}
OutputDir=..\release
OutputBaseFilename=AudioMonitorFunbox-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; The app's single-instance lock (Edition.cs): setup and uninstall ask you to exit the app first.
AppMutex=AudioMonitor_SingleInstance_7f3c1e
; Never close the app behind your back.
CloseApplications=no

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
Name: startup; Description: "Start with Windows"; Flags: unchecked

[Files]
Source: "..\portable versions\AudioFunbox.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: WantFunbox
Source: "..\portable versions\AudioMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: WantMonitor
Source: "..\release\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Switching editions: the other exe and its shortcuts go.
Type: files; Name: "{app}\AudioMonitor.exe"; Check: WantFunbox
Type: files; Name: "{app}\AudioFunbox.exe"; Check: WantMonitor
Type: files; Name: "{autoprograms}\AudioMonitor.lnk"; Check: WantFunbox
Type: files; Name: "{autoprograms}\AudioFunbox.lnk"; Check: WantMonitor
Type: files; Name: "{autodesktop}\AudioMonitor.lnk"; Check: WantFunbox
Type: files; Name: "{autodesktop}\AudioFunbox.lnk"; Check: WantMonitor

[Icons]
Name: "{autoprograms}\{code:EditionName}"; Filename: "{app}\{code:ExeName}"
Name: "{autodesktop}\{code:EditionName}"; Filename: "{app}\{code:ExeName}"; Tasks: desktopicon
; "Start with Windows": the same Startup-folder shortcut the app's own tray option uses (Startup.cs).
Name: "{userstartup}\AudioMonitorFunbox"; Filename: "{app}\{code:ExeName}"; WorkingDir: "{app}"; Tasks: startup

[Registry]
; Which edition is installed, so the next run of this installer knows what to offer.
Root: HKCU; Subkey: "Software\AudioMonitorFunbox"; ValueType: string; ValueName: "Edition"; ValueData: "{code:EditionKey}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\AudioMonitorFunbox"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"
Root: HKCU; Subkey: "Software\AudioMonitorFunbox"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"

[Run]
Filename: "{app}\{code:ExeName}"; Description: "Start {code:EditionName} now"; Flags: nowait postinstall skipifsilent

[Code]
const
  StateKey = 'Software\AudioMonitorFunbox';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E2C7B1D-4A9F-4C3E-9B58-1F0D7A3E52C4}_is1';

var
  EditionPage: TInputOptionWizardPage;
  MaintenancePage: TInputOptionWizardPage;
  Installed: String;        // '', 'Monitor' or 'Funbox'
  InstalledVersion: String;
  Chosen: String;           // 'Monitor' or 'Funbox'
  UninstallChosen: Boolean;

function ExeFor(Edition: String): String;
begin
  if Edition = 'Funbox' then Result := 'AudioFunbox.exe' else Result := 'AudioMonitor.exe';
end;

function NameFor(Edition: String): String;
begin
  if Edition = 'Funbox' then Result := 'AudioFunbox' else Result := 'AudioMonitor';
end;

function WantFunbox: Boolean;  begin Result := Chosen = 'Funbox';  end;
function WantMonitor: Boolean; begin Result := Chosen = 'Monitor'; end;
function ExeName(Param: String): String;     begin Result := ExeFor(Chosen);  end;
function EditionName(Param: String): String; begin Result := NameFor(Chosen); end;
function EditionKey(Param: String): String;  begin Result := Chosen;          end;

{ What's installed: trust the saved edition only if its exe is really there. }
procedure DetectInstalled;
var
  Dir: String;
begin
  Installed := '';
  InstalledVersion := '';
  if RegQueryStringValue(HKCU, StateKey, 'Edition', Installed) and
     RegQueryStringValue(HKCU, StateKey, 'InstallDir', Dir) then
  begin
    if ((Installed <> 'Monitor') and (Installed <> 'Funbox')) or not FileExists(AddBackslash(Dir) + ExeFor(Installed)) then
      Installed := ''
    else
      RegQueryStringValue(HKCU, StateKey, 'Version', InstalledVersion);
  end
  else
    Installed := '';
end;

function InitializeSetup(): Boolean;
var
  Param: String;
begin
  DetectInstalled;
  Param := Uppercase(ExpandConstant('{param:EDITION|}'));
  if Param = 'FUNBOX' then Chosen := 'Funbox'
  else if Param = 'MONITOR' then Chosen := 'Monitor'
  else if Installed <> '' then Chosen := Installed
  else Chosen := 'Monitor';
  Result := True;
end;

procedure InitializeWizard;
begin
  EditionPage := CreateInputOptionPage(wpWelcome,
    'Choose the version', 'Which version do you want to install?',
    'Both have the same settings, and you can switch later by running this installer again.',
    True, False);
  EditionPage.Add('AudioMonitor: speaker copy, microphone mixer for Discord, voice effects');
  EditionPage.Add('AudioFunbox: everything in AudioMonitor, plus the Pedalboard for guitar or bass (Rocksmith Real Tone cable, audio interface or PC jack)');
  if Chosen = 'Funbox' then EditionPage.SelectedValueIndex := 1 else EditionPage.SelectedValueIndex := 0;

  MaintenancePage := CreateInputOptionPage(wpWelcome,
    NameFor(Installed) + ' is already installed',
    'What do you want to do?',
    'Installed: ' + NameFor(Installed) + ' ' + InstalledVersion + '. This installer: {#AppVersion}. ' +
    'Your settings are kept, whatever you choose here.',
    True, False);
  if Installed = 'Funbox' then
  begin
    MaintenancePage.Add('Remove pedalboard (switch to AudioMonitor)');
    MaintenancePage.Add('Uninstall');
    MaintenancePage.Add('Update or repair AudioFunbox ({#AppVersion})');
  end
  else
  begin
    MaintenancePage.Add('Upgrade to the full version (AudioFunbox, adds the Pedalboard)');
    MaintenancePage.Add('Uninstall');
    MaintenancePage.Add('Update or repair AudioMonitor ({#AppVersion})');
  end;
  { Preselect what matches the current choice: "Update or repair" unless /EDITION asked for the other edition.
    Inno also "clicks Next" on this page in silent mode, so this is what a silent run does. }
  if Chosen = Installed then MaintenancePage.SelectedValueIndex := 2 else MaintenancePage.SelectedValueIndex := 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = EditionPage.ID then Result := Installed <> ''
  else if PageID = MaintenancePage.ID then Result := Installed = ''
  else if (PageID = wpSelectDir) or (PageID = wpSelectTasks) then Result := Installed <> '';   // keep folder and choices
end;

procedure StartUninstaller;
var
  Command: String;
  Code: Integer;
begin
  if not RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Command) then
  begin
    MsgBox('The uninstaller wasn''t found. Use Windows Settings > Apps instead.', mbError, MB_OK);
    Exit;
  end;
  if Exec(RemoveQuotes(Command), '', '', SW_SHOW, ewNoWait, Code) then
  begin
    UninstallChosen := True;
    WizardForm.Close;
  end
  else
    MsgBox('The uninstaller couldn''t start: ' + SysErrorMessage(Code), mbError, MB_OK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = EditionPage.ID then
  begin
    if EditionPage.SelectedValueIndex = 1 then Chosen := 'Funbox' else Chosen := 'Monitor';
  end
  else if CurPageID = MaintenancePage.ID then
  begin
    case MaintenancePage.SelectedValueIndex of
      0: if Installed = 'Funbox' then Chosen := 'Monitor' else Chosen := 'Funbox';
      1: begin StartUninstaller; Result := False; end;
      2: Chosen := Installed;
    end;
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if UninstallChosen then Confirm := False;   // closing because the uninstaller took over
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Action: String;
begin
  if Installed = '' then Action := 'Install ' + NameFor(Chosen)
  else if Chosen = Installed then Action := 'Update or repair ' + NameFor(Chosen)
  else if Chosen = 'Funbox' then Action := 'Upgrade to AudioFunbox (adds the Pedalboard)'
  else Action := 'Remove the Pedalboard: switch to AudioMonitor';
  Result := Action + ' {#AppVersion}' + NewLine + NewLine + MemoDirInfo;
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

{ ---------------- uninstall ---------------- }

{ Where a shortcut points, or '' if it can't be read. }
function ShortcutTarget(Path: String): String;
var
  Shell, Link: Variant;
begin
  Result := '';
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    Result := Link.TargetPath;
  except
    Result := '';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunValue, Lnk: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { "Start with Windows" set from the app's tray menu: remove it only if it starts this installed copy
      (a portable copy elsewhere keeps its own). }
    Lnk := ExpandConstant('{userstartup}\AudioMonitorFunbox.lnk');
    if FileExists(Lnk) and (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(ShortcutTarget(Lnk))) = 1) then
      DeleteFile(Lnk);
    { Versions up to 1.6.0 used a registry value instead: same rule. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AudioMonitor', RunValue) and
       (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(RunValue)) > 0) then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AudioMonitor');
  end;
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    if MsgBox('Also delete your settings (devices, mixer, effects and pedal settings)?' + #13#10#13#10 +
              'Choose No to keep them for a later install.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\AudioMonitor'), True, True, True);
  end;
end;
