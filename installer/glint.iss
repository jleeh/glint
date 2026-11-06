; Glint setup (Inno Setup 6). Build:
;   dotnet publish src/Glint/Glint.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o out\publish
;   ISCC.exe /DVersion=1.2.3 /DPublishDir=..\out\publish installer\glint.iss
; Output: out\Glint-Setup-<Version>.exe

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\out\publish"
#endif

[Setup]
AppId={{6B0C4E52-3F4A-4D7E-9A41-5C1E7B2D9F10}
AppName=Glint
AppVersion={#Version}
AppVerName=Glint {#Version}
AppPublisher=Glint contributors
DefaultDirName={autopf}\Glint
DefaultGroupName=Glint
DisableProgramGroupPage=yes
OutputDir=..\out
OutputBaseFilename=Glint-Setup-{#Version}
SetupIconFile=..\src\Glint\Resources\glint.ico
UninstallDisplayIcon={app}\Glint.exe
LicenseFile=..\LICENSE
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
; An upgrade (also Glint's own silent one-click update) keeps the tasks chosen last time.
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "pawnio"; Description: "Install PawnIO (CPU temperatures, motherboard fans, RAM lighting)"; Check: PawnIOMissing
Name: "webview2"; Description: "Install the Microsoft WebView2 runtime (needed for the dashboard)"; Check: WebView2Missing
Name: "disablesignalrgb"; Description: "Disable SignalRGB at startup (stops it and removes it from startup; does not uninstall it)"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\selftest.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "helper.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Glint runs elevated, so shortcuts start it through its scheduled task (no UAC prompt).
Name: "{autoprograms}\Glint"; Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""Glint"""; IconFilename: "{app}\Glint.exe"; Flags: runminimized
Name: "{autodesktop}\Glint"; Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""Glint"""; IconFilename: "{app}\Glint.exe"; Flags: runminimized; Tasks: desktopicon

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\helper.ps1"" -Action SignalRgb"; StatusMsg: "Disabling SignalRGB at startup..."; Flags: runhidden waituntilterminated; Tasks: disablesignalrgb
; Deliberately no skipifsilent: postinstall entries also run in /SILENT and /VERYSILENT mode, which is how Glint comes
; back after its one-click update (installer-test.yml checks the setup log for it).
Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""Glint"""; Description: "Start Glint now"; Flags: runhidden nowait postinstall

[Code]
function PawnIOMissing: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonpf64}\PawnIO\PawnIOLib.dll'));
end;

function WebView2Missing: Boolean;
var
  v: String;
begin
  // https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution#detect-if-a-webview2-runtime-is-already-installed
  Result := True;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0') then
    Result := False
  else if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0') then
    Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  // stop a running Glint (fresh install or upgrade) so files can be replaced
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/im Glint.exe /f', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1000);
  Result := '';
end;

procedure RunHelper(const Action, What: String);
var
  rc: Integer;
begin
  WizardForm.StatusLabel.Caption := What;
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
          '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\helper.ps1') + '" -Action ' + Action,
          '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0) then
    Exit;
  if rc = 2 then
    SuppressibleMsgBox('Could not download ' + Action + ' (no internet connection?). Setup will continue without it. ' +
           'You can install it later from its official site and Glint will pick it up.', mbInformation, MB_OK, IDOK)
  else
    SuppressibleMsgBox('Installing ' + Action + ' did not complete (code ' + IntToStr(rc) + '). Setup will continue without it.', mbInformation, MB_OK, IDOK);
end;

// The logon task is what starts Glint (elevated, no UAC prompt each boot), so a failure here must be visible.
procedure RegisterTasks();
var
  rc: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Registering startup tasks...';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
          '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\helper.ps1') + '" -Action Tasks -Dir "' + ExpandConstant('{app}') + '"',
          '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0) then
    Exit;
  SuppressibleMsgBox('Glint was installed, but its startup task could not be registered (code ' + IntToStr(rc) + '). ' +
         'Details are in %TEMP%\glint-setup-helper.log. You can run install.ps1 from the install folder to retry.', mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RegisterTasks();
    if WizardIsTaskSelected('webview2') then
      RunHelper('WebView2', 'Downloading and installing the WebView2 runtime...');
    if WizardIsTaskSelected('pawnio') then
      RunHelper('Pawnio', 'Downloading and installing PawnIO...');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  rc: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/im Glint.exe /f', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "Glint" /f', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "Glint selftest" /f', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Exec(ExpandConstant('{sys}\logman.exe'), 'stop Glint-Overlay -ets', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Sleep(500);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if SuppressibleMsgBox('Also delete your Glint settings and layouts (' + ExpandConstant('{userappdata}\Glint') + ')?' + #13#10 +
              'Choose No to keep them for a future install.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(ExpandConstant('{userappdata}\Glint'), True, True, True);
  end;
end;

