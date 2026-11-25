; SwVault setup wizard (Inno Setup 6). Built by scripts\package.ps1 from the staged package folder:
;   ISCC /DVersion=1.2.0 /DStage=<dist\SwVault-...> /DOutDir=<dist> /DOutName=SwVault-FSAE-1.2.0-Setup SwVault.iss
; It installs the same layout as install.ps1 (which still installs updates): C:\Program Files\SwVault
; with bin\ (agent, CLI, bundled Git, team.json) and addin\ (SOLIDWORKS add-in).
;
; The invite code travels in the file name (...-invite-XXXX-XXXX-XXXX.exe, as the server's download
; link names it), so new members don't have to type it.

#ifndef Version
  #define Version "0.1.0"
#endif
#ifndef Stage
  #error Pass /DStage=<staged package folder> (scripts\package.ps1 does).
#endif
#ifndef OutDir
  #define OutDir "."
#endif
#ifndef OutName
  #define OutName "SwVault-Setup"
#endif
#ifndef TeamName
  #define TeamName ""
#endif

[Setup]
AppId={{B0B5C9E2-6C0F-4B7B-9D56-5A1E3C1F7A10}
AppName=SwVault
AppVersion={#Version}
AppVerName=SwVault {#Version}
AppPublisher=SwVault
VersionInfoVersion={#Version}
; Fixed location: the agent only updates itself from Program Files, and install.ps1 updates this folder.
DefaultDirName={autopf}\SwVault
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ChangesEnvironment=yes
; SOLIDWORKS and the agent are handled below; Restart Manager would only offer to close them blindly.
CloseApplications=no
UninstallDisplayName=SwVault
UninstallDisplayIcon={app}\bin\SwVault.Agent.exe
OutputDir={#OutDir}
OutputBaseFilename={#OutName}

[Messages]
#if TeamName != ""
WelcomeLabel2=This installs [name/ver] for the {#TeamName} vault: check-out, check-in and history for SOLIDWORKS files.%n%nClose SOLIDWORKS before you continue.
#else
WelcomeLabel2=This installs [name/ver]: check-out, check-in and history for SOLIDWORKS files.%n%nClose SOLIDWORKS before you continue.
#endif
FinishedHeadingLabel=SwVault is installed
FinishedLabelNoIcons=First time on this PC? A SwVault window has opened: sign in, or choose "I'm new and have an invite" to make your account. Your team's files then download by themselves.%n%nOpen SOLIDWORKS afterwards: the SwVault tab and task pane are ready.
FinishedLabel=First time on this PC? A SwVault window has opened: sign in, or choose "I'm new and have an invite" to make your account. Your team's files then download by themselves.%n%nOpen SOLIDWORKS afterwards: the SwVault tab and task pane are ready.

[InstallDelete]
; Replace bin\ and addin\ entirely so an old bundled Git, team.json or interop DLL doesn't linger.
Type: filesandordirs; Name: "{app}\bin"
Type: filesandordirs; Name: "{app}\addin"

[Files]
Source: "{#Stage}\bin\*"; DestDir: "{app}\bin"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#Stage}\addin\*"; DestDir: "{app}\addin"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\SwVault"; Filename: "{app}\bin\SwVault.Agent.exe"; WorkingDir: "{app}\bin"

[UninstallDelete]
; Updates (install.ps1) add files this installer doesn't know about.
Type: filesandordirs; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function PowerShell(const Command: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
end;

{ 0 = not running, 1 = running with no window (didn't shut down completely), 2 = open. }
function SolidWorksState(): Integer;
begin
  Result := PowerShell('$p = @(Get-Process SLDWORKS -ErrorAction SilentlyContinue); if ($p.Count -eq 0) { exit 0 }; ' +
    'if (@($p | Where-Object { $_.MainWindowHandle -ne 0 }).Count -gt 0) { exit 2 }; exit 1');
  if Result < 0 then Result := 0;
end;

procedure EndSolidWorks();
var
  ResultCode, Tries: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM SLDWORKS.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Tries := 0;
  while (SolidWorksState() <> 0) and (Tries < 10) do
  begin
    Sleep(1000);
    Tries := Tries + 1;
  end;
end;

{ SOLIDWORKS keeps the add-in DLL loaded, so it has to be closed. A SOLIDWORKS that is still running
  without a window can't be closed by hand: offer to end it. True when it is safe to continue. }
function EnsureSolidWorksClosed(const Silent: Boolean): Boolean;
var
  State: Integer;
begin
  Result := False;
  while True do
  begin
    State := SolidWorksState();
    if State = 0 then
    begin
      Result := True;
      Exit;
    end;
    if State = 1 then
    begin
      { Nothing can be unsaved in a SOLIDWORKS without a window the user could have worked in. }
      if Silent then
        EndSolidWorks()
      else
        case TaskDialogMsgBox('SOLIDWORKS is still running in the background',
            'No SOLIDWORKS window is open, but SOLIDWORKS did not shut down completely, so SwVault can''t be installed yet.' + #13#10#13#10 +
            'Setup can end it for you.',
            mbConfirmation, MB_YESNOCANCEL, ['&End it and continue', 'Check &again', 'Cancel'], 0) of
          IDYES: EndSolidWorks();
          IDNO: ;
        else
          Exit;
        end;
    end
    else
    begin
      if Silent then Exit;
      if TaskDialogMsgBox('SOLIDWORKS is open',
          'Save your work and close SOLIDWORKS, then choose Try again.',
          mbInformation, MB_RETRYCANCEL, ['&Try again', 'Cancel'], 0) <> IDRETRY then
        Exit;
    end;
  end;
end;

{ The agent, the CLI and the bundled Git they start all run from the install folder and lock its files. }
procedure StopSwVaultProcesses();
begin
  PowerShell('$root = ''' + ExpandConstant('{app}') + '\''; ' +
    'Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue');
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not EnsureSolidWorksClosed(WizardSilent()) then
  begin
    Result := 'SOLIDWORKS is still running. Close it, then run this setup again.';
    Exit;
  end;
  StopSwVaultProcesses();
end;

{ "...-invite-K7QM-R3XT-9BWE.exe" (or "... (1).exe" after a second download) -> "K7QM-R3XT-9BWE". }
function InviteCodeFromFileName(): String;
var
  Name, Code: String;
  P, I: Integer;
begin
  Result := '';
  Name := Uppercase(ExtractFileName(ExpandConstant('{srcexe}')));
  P := Pos('INVITE-', Name);
  if P = 0 then Exit;
  Code := Copy(Name, P + 7, 14);
  if Length(Code) <> 14 then Exit;
  for I := 1 to 14 do
    if (I = 5) or (I = 10) then
    begin
      if Code[I] <> '-' then Exit;
    end
    else if Pos(Code[I], '23456789ABCDEFGHJKLMNPQRSTUVWXYZ') = 0 then
      Exit;
  Result := Code;
end;

procedure AddToMachinePath(const Dir: String);
var
  Path: String;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Path) then Path := '';
  if Pos(Lowercase(Dir), Lowercase(Path)) > 0 then Exit;
  if (Path <> '') and (Path[Length(Path)] <> ';') then Path := Path + ';';
  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Path + Dir);
end;

procedure RemoveFromMachinePath(const Dir: String);
var
  Path: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Path) then Exit;
  P := Pos(Lowercase(';' + Dir), Lowercase(Path));
  if P = 0 then Exit;
  Delete(Path, P, Length(Dir) + 1);
  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Path);
end;

function RegAsm(const Arguments: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{dotnet4064}\RegAsm.exe'),
    Arguments + ' /nologo "' + ExpandConstant('{app}\addin\SwVault.AddIn.dll') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Agent, Code: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;

  WizardForm.StatusLabel.Caption := 'Registering the SOLIDWORKS add-in...';
  if not RegAsm('/codebase') then
    SuppressibleMsgBox('SwVault is installed, but registering the SOLIDWORKS add-in failed, so SOLIDWORKS won''t show the SwVault tab.' + #13#10#13#10 +
      'Run this setup again. If it keeps happening, send your admin the output of "swvault doctor".', mbError, MB_OK, IDOK);
  AddToMachinePath(ExpandConstant('{app}\bin'));

  { The user's part runs as the person who started setup, not as the administrator who approved
    the prompt: start at sign-in, remember the invite code, and start the agent now. }
  Agent := ExpandConstant('{app}\bin\SwVault.Agent.exe');
  ExecAsOriginalUser(ExpandConstant('{sys}\reg.exe'),
    'add "HKCU\' + RunKey + '" /v SwVaultAgent /t REG_SZ /d "\"' + Agent + '\"" /f',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Code := InviteCodeFromFileName();
  if Code <> '' then
    ExecAsOriginalUser(ExpandConstant('{sys}\reg.exe'),
      'add "HKCU\Software\SwVault" /v InviteCode /t REG_SZ /d ' + Code + ' /f',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  ExecAsOriginalUser(Agent, '', ExpandConstant('{app}\bin'), SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

function InitializeUninstall(): Boolean;
begin
  Result := EnsureSolidWorksClosed(UninstallSilent());
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then Exit;
  StopSwVaultProcesses();
  RegAsm('/u');
  RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'SwVaultAgent');
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\SwVault', 'AgentPath');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\swvault');
  RemoveFromMachinePath(ExpandConstant('{app}\bin'));
end;
