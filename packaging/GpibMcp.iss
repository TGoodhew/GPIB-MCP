; GPIB MCP's installer. Built by packaging\Build-Installer.ps1, which stages the Release x86 build into one
; folder and passes the version and paths in as defines. Modelled on ManualForge's (TGoodhew/ManualForge#26).
;
; Per user, into %LOCALAPPDATA%\Programs\GpibMcp - the path the older install script, the release zip's
; instructions and every client config already use - so no administrator prompt.
;
; NI's drivers are a prerequisite this does NOT install. NI-VISA and NI-488.2 are large, need a restart,
; come under NI's licence, and the right NI-488.2 depends on which GPIB interface the user has. So Setup
; checks before installing anything, using the program's own `check` command (the same assembly binding
; the server does at startup, so "installed but too old" is caught too), and stops with what to install.
;
; Uninstalling undoes what Setup did: the client registrations it made (`setup mcp --remove` puts back any
; entry it replaced), the environment variable it set, and - if asked - the user's data folder.

#ifndef AppVersion
  #error Build with packaging\Build-Installer.ps1, which supplies the version and the staged build.
#endif

#define AppName "GPIB MCP"
#define Exe "GpibMcp.exe"

[Setup]
AppId={{5D6C3F1E-8B0A-4C7E-9E2B-6A1F0C4D7B93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Tony Goodhew
AppPublisherURL=https://github.com/TGoodhew/GPIB-MCP
AppSupportURL=https://github.com/TGoodhew/GPIB-MCP/issues
VersionInfoVersion={#FileVersion}
DefaultDirName={localappdata}\Programs\GpibMcp
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; Windows 10 1809 is the first to ship .NET Framework 4.7.2, which the server runs on.
MinVersion=10.0.17763
LicenseFile={#RepoDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=GpibMcp-Setup-{#AppVersion}
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; An upgrade first stops the running servers, naming the app that started each (PrepareToInstall, below).
; This catches anything else holding the program's files. Clients start the new server when next needed.
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes

[Tasks]
Name: mcp; Description: "Connect GPIB MCP to the AI apps on this PC (Claude, VS Code and others)"

[InstallDelete]
; An upgrade replaces the program whole, so nothing from an older build lingers beside the new one. The
; user's data - instrument database edits, assignments, logs - lives in %LOCALAPPDATA%\GpibMcp, not here.
Type: filesandordirs; Name: "{app}\*"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\GpibMcp"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\GpibMcp"; ValueType: string; ValueName: "Manuals"; ValueData: "{code:Manuals}"

[UninstallRun]
; Before the files go, while the program that knows how is still there.
Filename: "{app}\{#Exe}"; Parameters: "setup mcp --remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveMcp"

[Code]
const
  EnvironmentKey = 'Environment';
  StateKey = 'Software\GpibMcp';
  ManualsVariable = 'GPIB_MCP_MANUALS';
  ManualForgeKey = 'Software\ManualForge';
  NiVisaUrl = 'https://www.ni.com/en/support/downloads/drivers/download.ni-visa.html';
  Ni4882Url = 'https://www.ni.com/en/support/downloads/drivers/download.ni-488-2.html';
  { GpibMcp.exe check: 0 all present, 3 something missing. Anything else is the check itself failing. }
  CheckNiMissing = 3;

var
  ManualsPage: TInputQueryWizardPage;
  ManualsBrowse: TNewButton;
  Stopped, Registered, InstalledSummary: String;
  CheckFailed: Boolean;

{ ---------------------------------------------------------------- the NI check, before anything }

function JoinLines(const Lines: TArrayOfString): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    Result := Result + Lines[I] + #13#10;
end;

{ The check's report without its first line, "gpib-mcp <version>: NI drivers", and the blank line after it:
  the build is no help to someone being told what to install. The log keeps the whole report. }
function WithoutHeading(Report: String): String;
var
  P: Integer;
begin
  P := Pos(#13#10, Report);
  if P > 0 then Delete(Report, 1, P + 1);
  while Copy(Report, 1, 2) = #13#10 do Delete(Report, 1, 2);
  Result := Report;
end;

{ Runs the program's own check from the temporary folder, before a single file is installed. It loads exactly the NI
  assemblies this build references, as the server will, so it is right where a registry or folder test
  would not be: NI installed, but older than this build needs. }
function CheckNi(var Report: String; var NeedVisa, Need4882: Boolean): Boolean;
var
  Output: TExecOutput;
  Code: Integer;
  Dir: String;
begin
  // ExtractTemporaryFiles keeps the "{app}" of each file's destination as a folder of that name, so the
  // program and its DLLs are all extracted that way, to sit together. Concatenated, not expanded: {app}
  // has no value yet.
  ExtractTemporaryFiles('{app}\*.dll');
  ExtractTemporaryFiles('{app}\{#Exe}');
  ExtractTemporaryFiles('{app}\{#Exe}.config');
  Dir := ExpandConstant('{tmp}') + '\{app}';
  CheckFailed := False;

  if not ExecAndCaptureOutput(Dir + '\{#Exe}', 'check', Dir, SW_HIDE, ewWaitUntilTerminated, Code, Output) then
  begin
    Report := 'The check could not be run: ' + SysErrorMessage(Code);
    CheckFailed := True;
    Result := False;
    Exit;
  end;

  Report := JoinLines(Output.StdOut);
  Log('NI check (exit ' + IntToStr(Code) + '):' + #13#10 + Report + JoinLines(Output.StdErr));
  NeedVisa := Pos(NiVisaUrl, Report) > 0;
  Need4882 := Pos(Ni4882Url, Report) > 0;
  if (Code <> 0) and (Code <> CheckNiMissing) then
  begin
    { Not "missing" but the check falling over. Said as that, with what it printed, rather than as
      missing drivers: telling someone with NI installed to install it would send them the wrong way. }
    Report := Report + JoinLines(Output.StdErr);
    CheckFailed := True;
  end;
  Result := Code = 0;
end;

function InitializeSetup: Boolean;
var
  Report, Message: String;
  NeedVisa, Need4882: Boolean;
  Ignored: Integer;
begin
  Result := CheckNi(Report, NeedVisa, Need4882);
  if Result then Exit;

  if CheckFailed then
  begin
    Log('Stopping: the NI driver check failed.');
    SuppressibleMsgBox('Setup could not check whether NI''s drivers are installed, so nothing was installed:' + #13#10#13#10 +
      Report + #13#10 + 'Please report this at https://github.com/TGoodhew/GPIB-MCP/issues with the text above.',
      mbCriticalError, MB_OK, IDOK);
    Exit;
  end;

  Message := '{#AppName} talks to instruments through NI''s drivers, and this PC does not have all of them yet:' + #13#10#13#10 +
    WithoutHeading(Report) + #13#10 +
    'Install NI-VISA and NI-488.2, each with .NET support, then run Setup again. NI''s installers need ' +
    'administrator rights and usually a restart.' + #13#10#13#10 +
    'Open NI''s download page now?';

  if WizardSilent then
    Log('Stopping: the NI drivers are not installed.')
  else if SuppressibleMsgBox(Message, mbError, MB_YESNO, IDNO) = IDYES then
  begin
    if NeedVisa then ShellExec('open', NiVisaUrl, '', '', SW_SHOWNORMAL, ewNoWait, Ignored);
    if Need4882 then ShellExec('open', Ni4882Url, '', '', SW_SHOWNORMAL, ewNoWait, Ignored);
  end;
end;

{ ---------------------------------------------------------------- the manual folder }

{ A DWORD this installer set to 1 in its state key, to remember something it did. }
function Flag(const Name: String): Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKCU, StateKey, Name, Value) and (Value = 1);
end;

{ The folder, or '' for none: a blank box means no manuals (#159). }
function Manuals(Param: String): String;
begin
  Result := Trim(ManualsPage.Values[0]);
end;

{ ManualForge's library, or '' when ManualForge isn't installed. Its installer writes this value and removes
  the whole key on uninstall, so the value being there means ManualForge is installed. }
function ManualForgeLibrary: String;
begin
  if not RegQueryStringValue(HKCU, ManualForgeKey, 'Library', Result) then
    Result := '';
  Result := Trim(Result);
end;

procedure BrowseManuals(Sender: TObject);
var
  Folder: String;
begin
  Folder := Manuals('');
  if BrowseForFolder('Choose the folder that holds your instrument manuals.', Folder, False) then
    ManualsPage.Values[0] := Folder;
end;

procedure InitializeWizard;
var
  ForgeLibrary, Initial, Description: String;
  Edit: TPasswordEdit;
begin
  { One library for GPIB MCP and ManualForge (#159): ManualForge's when it is installed, else the user's
    choice - which may be none. /MANUALS="D:\Manuals" answers the page on the command line. }
  ForgeLibrary := ManualForgeLibrary;
  Initial := Trim(ExpandConstant('{param:MANUALS|}'));
  if Initial = '' then
    Initial := ForgeLibrary;

  Description := '{#AppName} searches this folder for a command its database does not have, and quotes the ' +
    'page it found. It only reads the folder.' + #13#10#13#10;
  { Only when the box starts with ManualForge's folder: a /MANUALS= naming another isn't ManualForge's library. }
  if (ForgeLibrary <> '') and
     (CompareText(RemoveBackslashUnlessRoot(Initial), RemoveBackslashUnlessRoot(ForgeLibrary)) = 0) then
    Description := Description + 'This is ManualForge''s library, so both search the same manuals and ' +
      '{#AppName} can use ManualForge''s index to find the right one.' + #13#10#13#10;
  Description := Description + 'No folder of manuals? Leave this blank. {#AppName} works without one; it just ' +
    'can''t look up a command its database doesn''t have.';

  ManualsPage := CreateInputQueryPage(wpSelectTasks, 'Your manuals',
    'Which folder holds your instrument manuals?', Description);
  ManualsPage.Add('Manual folder (or blank for none):', False);

  { A text box rather than Inno's folder page, which refuses an empty box. Browse sits beside it. }
  Edit := ManualsPage.Edits[0];
  ManualsBrowse := TNewButton.Create(ManualsPage);
  ManualsBrowse.Parent := ManualsPage.Surface;
  ManualsBrowse.Caption := SetupMessage(msgButtonWizardBrowse);
  ManualsBrowse.Width := WizardForm.NextButton.Width;
  ManualsBrowse.Height := WizardForm.NextButton.Height;
  ManualsBrowse.Left := ManualsPage.SurfaceWidth - ManualsBrowse.Width;
  ManualsBrowse.Top := Edit.Top + (Edit.Height - ManualsBrowse.Height) div 2;
  ManualsBrowse.OnClick := @BrowseManuals;
  Edit.Width := ManualsBrowse.Left - ScaleX(8) - Edit.Left;

  ManualsPage.Values[0] := Initial;
end;

{ A folder that isn't there is refused; blank is fine. Not in a silent install, which has no one to ask. }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ManualsPage.ID) and not WizardSilent and (Manuals('') <> '') and not DirExists(Manuals('')) then
  begin
    MsgBox('There is no folder at:' + #13#10 + Manuals('') + #13#10#13#10 +
      'Choose the folder that holds your manuals, or leave the box blank for none.', mbError, MB_OK);
    Result := False;
  end;
end;

{ ---------------------------------------------------------------- an upgrade: what is running, and whose }

{ The app a process belongs to, from its program. Claude Code inside VS Code is claude.exe under the VS
  Code extension, so it is named before the walk reaches Code.exe. }
function KnownClient(const Name, Path: String): String;
begin
  Result := '';
  if Name = 'claude.exe' then
  begin
    if (Pos('\windowsapps\claude_', Path) > 0) or (Pos('\anthropicclaude\', Path) > 0) then
      Result := 'Claude Desktop'
    else
      Result := 'Claude Code';
  end
  else if Name = 'code.exe' then Result := 'VS Code'
  else if Name = 'code - insiders.exe' then Result := 'VS Code Insiders'
  else if Name = 'devenv.exe' then Result := 'Visual Studio'
  else if Name = 'cursor.exe' then Result := 'Cursor'
  else if Name = 'windsurf.exe' then Result := 'Windsurf'
  else if Name = 'lm studio.exe' then Result := 'LM Studio'
  else if Name = 'codex.exe' then Result := 'Codex'
  else if Name = 'explorer.exe' then Result := 'Windows (started by you)';
end;

{ Walks up from a process to the first app it knows, or names the parent when it knows none. }
function OwnerOf(Service: Variant; Pid: Integer): String;
var
  Found, Item: Variant;
  Level: Integer;
  Name, Path, Known, Text: String;
begin
  Result := 'unknown';
  for Level := 1 to 5 do
  begin
    Found := Service.ExecQuery(Format('SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = %d', [Pid]));
    if Found.Count = 0 then Exit;
    Pid := Found.ItemIndex(0).ParentProcessId;
    Found := Service.ExecQuery(Format('SELECT Name, ExecutablePath FROM Win32_Process WHERE ProcessId = %d', [Pid]));
    if Found.Count = 0 then Exit;
    Item := Found.ItemIndex(0);
    Text := Item.Name;
    Name := Lowercase(Text);
    if VarIsNull(Item.ExecutablePath) then Path := '' else
    begin
      Path := Item.ExecutablePath;
      Path := Lowercase(Path);
    end;
    Known := KnownClient(Name, Path);
    if Known <> '' then
    begin
      Result := Known;
      Exit;
    end;
    if Level = 1 then Result := Text;
  end;
end;

{ The servers running from this install's folder, each with the app that started it. }
function FindRunning(var Pids: TArrayOfInteger): String;
var
  Locator, Service, Found, Item: Variant;
  I, N: Integer;
  App, Path: String;
begin
  Result := '';
  SetArrayLength(Pids, 0);
  App := Lowercase(AddBackslash(ExpandConstant('{app}')));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''{#Exe}''');
    for I := 0 to Found.Count - 1 do
    begin
      Item := Found.ItemIndex(I);
      if VarIsNull(Item.ExecutablePath) then Continue;
      Path := Item.ExecutablePath;
      Path := Lowercase(Path);
      if Copy(Path, 1, Length(App)) <> App then Continue;
      N := GetArrayLength(Pids);
      SetArrayLength(Pids, N + 1);
      Pids[N] := Item.ProcessId;
      Result := Result + #13#10 + Format('  {#Exe} (process %d), started by %s', [Pids[N], OwnerOf(Service, Pids[N])]);
    end;
  except
    Log('Could not list running processes: ' + GetExceptionMessage);
  end;
end;

{ Says what is running and whose it is, then stops it. False if the user would rather it kept running. }
function StopRunning(Silent: Boolean; const Why: String): Boolean;
var
  Pids: TArrayOfInteger;
  Running: String;
  I, Code: Integer;
begin
  Result := True;
  Running := FindRunning(Pids);
  if Running = '' then Exit;

  Log('{#AppName} is running:' + Running);
  if not Silent then
    if MsgBox('{#AppName} is running:' + Running + #13#10#13#10 + Why, mbConfirmation, MB_OKCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;

  for I := 0 to GetArrayLength(Pids) - 1 do
    Exec(ExpandConstant('{sys}\taskkill.exe'), Format('/PID %d /F', [Pids[I]]), '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1000);
  Stopped := Running;
  Log('Stopped:' + Stopped);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopRunning(WizardSilent,
      'Setup stops these to replace the program. Any instrument operation in progress is interrupted. The apps ' +
      'that started them start the new one the next time they need it; if one shows an error for GPIB, restart that app.') then
    Result := '{#AppName} is still running, so nothing was changed. Close the apps using it and run Setup again.';
end;

{ ---------------------------------------------------------------- after the files: register, and say so }

function Quoted(const S: String): String;
begin
  Result := '"' + S + '"';
end;

procedure RegisterWithClients;
var
  Output: TExecOutput;
  Code: Integer;
  Arguments: String;
begin
  if Manuals('') <> '' then
    Arguments := 'setup mcp --manuals ' + Quoted(Manuals(''))
  else
    Arguments := 'setup mcp --no-manuals';

  if ExecAndCaptureOutput(ExpandConstant('{app}\{#Exe}'), Arguments, ExpandConstant('{app}'), SW_HIDE,
      ewWaitUntilTerminated, Code, Output) then
  begin
    Registered := JoinLines(Output.StdOut);
    Log('setup mcp (exit ' + IntToStr(Code) + '):' + #13#10 + Registered + JoinLines(Output.StdErr));
  end
  else
    Log('setup mcp could not be run: ' + SysErrorMessage(Code));
end;

{ Which build is installed, from the program itself: in the log always, and on the last page. }
function InstalledBuild: String;
var
  Output: TExecOutput;
  Code: Integer;
begin
  if InstalledSummary = '' then
  begin
    if ExecAndCaptureOutput(ExpandConstant('{app}\{#Exe}'), '--version', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then
      if GetArrayLength(Output.StdOut) > 0 then
        InstalledSummary := Output.StdOut[0];
    Log('Installed: ' + InstalledSummary);
  end;
  Result := InstalledSummary;
end;

{ The apps it was connected to, from setup mcp's output: the lines that say what happened to an app. }
function RegisteredSummary: String;
var
  Text, Line: String;
  P: Integer;
begin
  Result := '';
  Text := Registered;
  while Text <> '' do
  begin
    P := Pos(#13#10, Text);
    if P = 0 then P := Length(Text) + 1;
    Line := Copy(Text, 1, P - 1);
    Delete(Text, 1, P + 1);
    { "  VS Code   registered" - an app's own line - not the location indented under it. }
    if (Copy(Line, 1, 2) = '  ') and (Copy(Line, 3, 1) <> ' ') and (Pos('not installed', Line) = 0) then
      Result := Result + #13#10 + Line;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Summary: String;
begin
  if CurPageID <> wpFinished then Exit;

  Summary := InstalledBuild;
  if Registered <> '' then
    Summary := Summary + #13#10#13#10 + 'Connected to:' + RegisteredSummary + #13#10#13#10 +
      'Restart those apps to pick it up.';
  if Stopped <> '' then
    Summary := Summary + #13#10#13#10 + 'Stopped for the upgrade:' + Stopped;
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Summary;
  WizardForm.FinishedLabel.AdjustHeight;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Existing: String;
begin
  if CurStep = ssPostInstall then
  begin
    { The manual folder, for anything that starts the server without a client's env block - the HTTP
      launcher, a terminal. Someone else's variable is left alone; ours follows this answer (#159), so a new
      folder replaces the old one and a blank box removes it. Uninstalling takes away only what this put there. }
    if Flag('SetManualsVariable') then
    begin
      if Manuals('') <> '' then
        RegWriteStringValue(HKCU, EnvironmentKey, ManualsVariable, Manuals(''))
      else
      begin
        RegDeleteValue(HKCU, EnvironmentKey, ManualsVariable);
        RegDeleteValue(HKCU, StateKey, 'SetManualsVariable');
      end;
    end
    else if (Manuals('') <> '') and not RegQueryStringValue(HKCU, EnvironmentKey, ManualsVariable, Existing) then
    begin
      RegWriteStringValue(HKCU, EnvironmentKey, ManualsVariable, Manuals(''));
      RegWriteDWordValue(HKCU, StateKey, 'SetManualsVariable', 1);
    end;

    if WizardIsTaskSelected('mcp') then
      RegisterWithClients;
  end;

  { A silent install has no last page, so the log is the only place this is said. }
  if CurStep = ssDone then
    InstalledBuild;
end;

{ ---------------------------------------------------------------- uninstalling }


function InitializeUninstall: Boolean;
begin
  Result := StopRunning(UninstallSilent,
    'Uninstalling stops these first. The apps that started them no longer have {#AppName} once it is removed.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Read before the [Registry] key goes. }
    if Flag('SetManualsVariable') then
      RegDeleteValue(HKCU, EnvironmentKey, ManualsVariable);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    { Kept unless asked: it holds the user's own work - instrument database edits and corrections,
      which instruments are at which address - as well as logs and the bytes of past captures. }
    Data := ExpandConstant('{localappdata}\GpibMcp');
    if DirExists(Data) and not UninstallSilent then
      if MsgBox('Also remove {#AppName}''s data?' + #13#10#13#10 +
          'It holds your instrument database edits, which instrument is at which address, logs and past captures:' + #13#10 +
          '  ' + Data + #13#10#13#10 +
          'Keep it to pick up where you left off if you install {#AppName} again.',
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
