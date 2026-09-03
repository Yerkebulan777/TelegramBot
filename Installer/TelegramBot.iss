; TelegramBot installer — Server as a Windows Service, Worker as a Task
; Scheduler job (see below for why Worker can't be a plain service).
;
; Prerequisite (run before compiling this script):
;   dotnet publish TelegramBot.Server\TelegramBot.Server.csproj -c Release -o Installer\publish\Server
;   dotnet publish TelegramBot.Worker\TelegramBot.Worker.csproj -c Release -o Installer\publish\Worker
;   dotnet publish Installer\GrantLogonRight\GrantLogonRight.csproj -c Release -o Installer\publish\GrantLogonRight
;
; What this script does that a plain "sc.exe create" walkthrough doesn't:
;   - lets the admin pick Server / Worker / both
;   - resolves a mapped drive letter (e.g. B:) to its UNC path AT INSTALL TIME,
;     on the admin's own session where the mapping actually exists (see chat:
;     services can't see per-user drive mappings, so this must happen here,
;     not at service startup)
;   - writes that UNC path into each component's appsettings.Local.json
;   - registers Server under a dedicated account with auto-restart
;   - registers Worker as an interactive Task Scheduler job, NOT a service:
;     Worker launches Revit, and Windows services run in Session 0, isolated
;     from any real desktop since Vista — a service-spawned Revit renders on
;     an invisible desktop no human can see or click through. A logon-trigger
;     task runs in the actual user's session instead, so Revit shows up
;     normally. Trade-off: the target account must stay logged on (configure
;     auto-logon on dedicated machines) — Worker won't run between reboot and
;     next interactive logon.

#define AppName "TelegramBot"
#define ServerExe "TelegramBot.Server.exe"
#define WorkerExe "TelegramBot.Worker.exe"
#define ServerSvc "TelegramBotServer"
#define WorkerSvc "TelegramBotWorker"

[Setup]
AppName={#AppName}
AppVersion=1.0
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename={#AppName}Setup
WizardStyle=modern
SolidCompression=no
Compression=lzma2/max
#ifdef EnableCodeSigning
SignTool=TelegramBotInternalSign
SignedUninstaller=yes
#endif

[Types]
Name: "full"; Description: "Server + Worker (оба)"
Name: "server"; Description: "Только Server"
Name: "worker"; Description: "Только Worker"
Name: "custom"; Description: "Выборочно"; Flags: iscustom

[Components]
Name: "server"; Description: "TelegramBot Server"; Types: full server
Name: "worker"; Description: "TelegramBot Worker"; Types: full worker

[Files]
Source: "publish\Server\*"; DestDir: "{app}\Server"; Components: server; Flags: recursesubdirs ignoreversion
Source: "publish\Worker\*"; DestDir: "{app}\Worker"; Components: worker; Flags: recursesubdirs ignoreversion
Source: "publish\GrantLogonRight\*"; DestDir: "{app}\Tools"; Components: server; Flags: recursesubdirs ignoreversion

[Code]
var
  AccountPage: TInputQueryWizardPage;
  PathPage: TInputDirWizardPage;
  TelegramPage: TInputQueryWizardPage;
  ResolvedPath: String;

{ Resolves "B:" (or "B:\", which is what CreateInputDirPage actually hands
  back for a drive root — it normalizes with a trailing backslash) to
  "\\server\share". Two mechanisms, tried in order:
  1. ExpandUNCFileName — built-in Pascal Script function using the CURRENT
     process's drive map (no manual WNetGetConnection FFI — that was the
     original bug: Inno 6 Pascal strings are Unicode, WNetGetConnectionA is
     ANSI, buffer marshalling silently failed). Works only when the mapping
     is visible to THIS process — and a UAC-elevated installer does NOT
     inherit the interactive session's mappings unless EnableLinkedConnections=1
     (HKLM ...\Policies\System; written below in [Registry], takes effect on
     next logon). On a fresh machine this mechanism alone kept failing.
  2. HKCU\Network\<letter>\RemotePath — where Explorer / "net use" record
     classic mapped drives. HKCU is the same hive for a consent-elevated
     user, so the mapping is readable even when invisible to this process
     (verified on a live box: HKCU\Network\z → RemotePath). This is what
     makes resolution work on the very first install, no re-login needed.
     Caveat: GPP (Group Policy Preferences) drive maps don't write this
     key — for those only mechanism 1 helps, after the re-login.
  Subpaths resolve too ("Z:\01_PROJECT" → "\\server\share\01_PROJECT") —
  an unresolved raw drive-letter path would be invisible to the service.
  Success is False only for a bare drive root that neither mechanism could
  map to a network path (not connected, GPP-mapped before the re-login, or
  a genuinely local disk); the caller must block on that — an unresolved
  drive letter is invisible to a Windows service and is exactly what caused
  the Server outage this was written to fix, so this can no longer be a
  soft warning that lets Next through.
  An input that's already a UNC path always succeeds untouched. }
function ResolveDriveToUNC(const Input: String; var Success: Boolean): String;
var
  Trimmed, DriveSpec, UncPath, SubPath: String;
begin
  Trimmed := Trim(Input);
  Result := ExpandUNCFileName(Trimmed);

  { Mechanism 2 fires when the input is a drive-letter path and mechanism 1
    left the letter unresolved. Registry keys are case-insensitive, so the
    letter's case doesn't matter. }
  if (Length(Trimmed) >= 2) and (Trimmed[2] = ':') and
     (CompareText(Copy(Result, 1, 2), Copy(Trimmed, 1, 2)) = 0) then
  begin
    SubPath := '';
    if (Length(Trimmed) > 3) and (Trimmed[3] = '\') then
      SubPath := Copy(Trimmed, 3, Length(Trimmed) - 2);
    if RegQueryStringValue(HKCU, 'Network\' + Trimmed[1], 'RemotePath', UncPath) and
       (Trim(UncPath) <> '') then
      Result := Trim(UncPath) + SubPath;
  end;

  DriveSpec := Trimmed;
  if (Length(DriveSpec) = 3) and (DriveSpec[2] = ':') and (DriveSpec[3] = '\') then
    DriveSpec := Copy(DriveSpec, 1, 2);
  Success := not ((Length(DriveSpec) = 2) and (DriveSpec[2] = ':') and
    (CompareText(Copy(Result, 1, 2), DriveSpec) = 0));
end;

procedure InitializeWizard;
begin
  AccountPage := CreateInputQueryPage(wpSelectComponents,
    'Учётная запись службы', 'Под какой учёткой будут работать Server/Worker?',
    'Не используйте LocalSystem/NetworkService — им нужен явный доступ к сетевой шаре. ' +
    'По умолчанию подставлена текущая учётка (у неё уже есть доступ к сетевой шаре в этой сессии). ' +
    'Пароль Windows не хранит в доступном виде — введите его вручную (нужен только для Server; ' +
    'Worker запускается как задача планировщика в сессии этого пользователя при входе в систему — ' +
    'машина должна оставаться залогиненной под этой учёткой, иначе Worker не стартует).');
  AccountPage.Add('Имя учётной записи:', False);
  AccountPage.Add('Пароль:', True);
  AccountPage.Values[0] := ExpandConstant('{%USERDOMAIN}\{username}');

  PathPage := CreateInputDirPage(AccountPage.ID,
    'Путь к файловой шаре', 'Где лежат файлы Revit/проектов?',
    'Можно ввести букву смонтированного диска (B:) — она будет преобразована в UNC-путь ' +
    'на основе текущей сессии, либо нажать «Обзор» и выбрать сетевую папку (\\сервер\шара) напрямую.',
    True, '');
  PathPage.Add('Путь:');
  PathPage.Values[0] := 'B:';

  TelegramPage := CreateInputQueryPage(PathPage.ID,
    'Настройки Telegram-бота', 'Только для Server — токен бота.',
    'Токен выдаёт @BotFather.');
  TelegramPage.Add('Bot token:', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = TelegramPage.ID then
    Result := not WizardIsComponentSelected('server');
end;

{ Best-effort hints for the blocked-path message: UNC paths this user is
  known to use, so the admin can type one directly instead of hunting for it.
  Both sources are readable from the elevated process:
  - HKCU\Network\<letter> — persistent mappings (letter → UNC lines);
  - MountPoints2 "##server#share" keys — shares the shell has touched,
    which includes targets of GPP/logon-script mappings whose letter→UNC
    binding lives only in the interactive session's WNet table and is
    fundamentally invisible to an elevated process (verified on a live box:
    nothing in HKCU records B:'s letter binding; the share path itself
    shows up in MountPoints2).
  Duplicates between the two sources are suppressed by substring check. }
function KnownShareHint: String;
var
  Names: TArrayOfString;
  I: Integer;
  Unc, Share: String;
begin
  Result := '';
  if RegGetSubkeyNames(HKCU, 'Network', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(HKCU, 'Network\' + Names[I], 'RemotePath', Unc) and (Trim(Unc) <> '') then
        Result := Result + #13#10 + '  ' + Uppercase(Names[I]) + ':  →  ' + Trim(Unc);
  if RegGetSubkeyNames(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 2) = '##' then
      begin
        Share := '\\' + Copy(Names[I], 3, Length(Names[I]) - 2);
        StringChangeEx(Share, '#', '\', True);
        if Pos(Share, Result) = 0 then
          Result := Result + #13#10 + '  ' + Share;
      end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResolveOk: Boolean;
  Hint: String;
begin
  Result := True;
  if CurPageID = PathPage.ID then
  begin
    if Trim(PathPage.Values[0]) = '' then
    begin
      MsgBox('Укажите путь к файловой шаре.', mbError, MB_OK);
      Result := False;
      exit;
    end;
    ResolvedPath := ResolveDriveToUNC(PathPage.Values[0], ResolveOk);
    if not ResolveOk then
    begin
      Hint := KnownShareHint;
      if Hint <> '' then
        Hint := #13#10#13#10 + 'Известные сетевые пути этого пользователя:' + Hint;
      MsgBox('Не удалось определить сетевой путь для диска ' + Trim(PathPage.Values[0]) + '. ' +
        'Проверьте, что диск подключён (net use), либо введите UNC-путь напрямую (\\сервер\шара).' + Hint,
        mbError, MB_OK);
      Result := False;
      exit;
    end;
  end
  else if CurPageID = AccountPage.ID then
  begin
    if Trim(AccountPage.Values[0]) = '' then
    begin
      MsgBox('Укажите имя учётной записи.', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = TelegramPage.ID then
  begin
    if Trim(TelegramPage.Values[0]) = '' then
    begin
      MsgBox('Укажите токен бота.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
end;

{ Server ships appsettings.Local.json with other keys already in it (token,
  connection string, RootPath) — patch just the matched key's value in place,
  preserving whatever trailing comma the original line had (JSON syntax
  breaks if a comma is added/dropped on the wrong line). Worker has no
  Local.json yet, so it's cheaper to just write a fresh minimal one. }
procedure PatchJsonKey(const JsonPath, KeyName, NewValue: String; QuoteValue: Boolean);
var
  Lines: TArrayOfString;
  I: Integer;
  Found: Boolean;
  ValuePart, TrimmedLine, Suffix: String;
begin
  if QuoteValue then
    ValuePart := '"' + JsonEscape(NewValue) + '"'
  else
    ValuePart := NewValue;
  Found := False;
  if LoadStringsFromFile(JsonPath, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      if Pos('"' + KeyName + '"', Lines[I]) > 0 then
      begin
        TrimmedLine := TrimRight(Lines[I]);
        if (Length(TrimmedLine) > 0) and (TrimmedLine[Length(TrimmedLine)] = ',') then
          Suffix := ','
        else
          Suffix := '';
        Lines[I] := '    "' + KeyName + '": ' + ValuePart + Suffix;
        Found := True;
      end;
    end;
    if Found then
      SaveStringsToFile(JsonPath, Lines, False)
    else
      MsgBox('Не найдена строка "' + KeyName + '" в ' + JsonPath + ' — значение не записано, ' +
        'настройте его вручную.', mbError, MB_OK);
  end
  else
    MsgBox('Файл не найден: ' + JsonPath, mbError, MB_OK);
end;

procedure WriteWorkerRootPath(const JsonPath, NewPath: String);
begin
  SaveStringToFile(JsonPath,
    '{' + #13#10 +
    '  "FileSystem": {' + #13#10 +
    '    "RootPath": "' + JsonEscape(NewPath) + '"' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10, False);
end;

function QuoteArg(const S: String): String;
begin
  Result := '"' + S + '"';
end;

procedure GrantAccess(const Path, Account: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\icacls.exe'),
    Format('%s /grant %s:(OI)(CI)F', [QuoteArg(Path), QuoteArg(Account)]),
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Single, shared mechanic for every privileged CLI call in this script
  (sc.exe, schtasks.exe, our own GrantLogonRight.exe helper): run it through
  cmd.exe with stdout+stderr redirected to a log file, and on failure show
  that log alongside the exit code. A bare "код 1" says nothing about *why*
  a command failed — this used to be a one-off hack specific to the logon-
  right helper; it's the one command runner everything else should go
  through too, instead of each caller improvising its own capture.
  cmd.exe's /c quoting quirk: when the argument starts and ends with a
  quote, cmd strips exactly that outer pair before parsing the rest — same
  trick RegisterWorkerTask uses for schtasks /tr below. }
function RunAdminCommand(const Exe, Args, ErrorContext: String): Boolean;
var
  LogPath, CmdArgs, LogText: String;
  LogTextA: AnsiString;
  ResultCode: Integer;
begin
  LogPath := ExpandConstant('{tmp}\admin_cmd.log');
  CmdArgs := Format('/c ""%s" %s > "%s" 2>&1"', [ExpandConstant(Exe), Args, LogPath]);
  Result := Exec(ExpandConstant('{cmd}'), CmdArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if Result then exit;

  { LoadStringFromFile reads into AnsiString (unlike LoadStringsFromFile's
    per-line Unicode split) — convert once here rather than threading
    AnsiString through the rest of the function. }
  LoadStringFromFile(LogPath, LogTextA);
  LogText := Trim(String(LogTextA));

  if LogText <> '' then
    MsgBox(ErrorContext + ' (код ' + IntToStr(ResultCode) + '):' + #13#10#13#10 + LogText, mbError, MB_OK)
  else
    MsgBox(ErrorContext + ' (код ' + IntToStr(ResultCode) + ').', mbError, MB_OK);
end;

{ Grants "Log on as a service" (SeServiceLogonRight) to Account — the same
  right the Services GUI grants silently via LsaAddAccountRights when you set
  a service's logon account by hand; sc.exe create skips that step entirely,
  which is the actual reason a fresh install otherwise needs a manual
  secpol.msc visit. Calls the LSA API directly through the bundled
  GrantLogonRight.exe helper (Installer\GrantLogonRight) rather than hand-
  patching a secedit-exported INF template — that approach turned out too
  fragile to debug remotely (encoding mismatches, missing sections, and a
  bare exit code with an empty log).
  Best-effort only: if a domain GPO enforces this right, it overwrites the
  local grant again on its own refresh cycle — no local fix survives that,
  see README troubleshooting section. }
procedure GrantServiceLogonRight(const Account: String);
begin
  RunAdminCommand('{app}\Tools\GrantLogonRight.exe', QuoteArg(Account),
    'Не удалось выдать право "Вход в качестве службы"');
end;

function RegisterService(const SvcName, DisplayName, ExePath, Account, Password: String): Boolean;
var
  CreateCmd: String;
begin
  CreateCmd := Format('create %s binPath= %s obj= %s password= %s start= delayed-auto DisplayName= %s', [
    SvcName, QuoteArg(ExePath), QuoteArg(Account), QuoteArg(Password), QuoteArg(DisplayName)]);
  Result := RunAdminCommand('{sys}\sc.exe', CreateCmd,
    'Не удалось создать службу ' + SvcName + '. Проверьте имя учётной записи и пароль');
  if not Result then exit;
  { Best-effort: auto-restart policy isn't required for the service to work. }
  RunAdminCommand('{sys}\sc.exe',
    'failure ' + SvcName + ' reset= 86400 actions= restart/5000/restart/10000/restart/30000',
    'Не удалось настроить авто-рестарт для ' + SvcName);
end;

{ Worker runs Revit, which needs a real, visible desktop — a Windows Service
  can't provide one (Session 0 isolation, no "interact with desktop" option
  since Vista). /it runs the task in Account's own interactive logon session
  instead of headless, so Revit renders normally.
  ponytail: no crash-auto-restart — schtasks' basic switches don't expose an
  equivalent of sc.exe's "failure actions"; add an XML-imported task with
  <RestartOnFailure> if that's needed later. }
function RegisterWorkerTask(const TaskName, ExePath, Account: String): Boolean;
var
  CreateCmd: String;
begin
  { schtasks' /tr parsing (unlike sc.exe's binPath) truncates at the first
    space when the path is only wrapped in one layer of quotes — it needs the
    quote characters embedded in the value itself, hence the escaped \"...\". }
  { /delay 0000:05 — 5s after logon (which happens at boot via auto-logon)
    gives the desktop/network a moment to settle before Worker/Revit starts. }
  CreateCmd := Format('/create /tn %s /tr "\"%s\"" /sc onlogon /ru %s /it /rl highest /delay 0000:05 /f', [
    QuoteArg(TaskName), ExePath, QuoteArg(Account)]);
  Result := RunAdminCommand('{sys}\schtasks.exe', CreateCmd,
    'Не удалось создать задачу планировщика для Worker');
  if not Result then exit;
  { Best-effort immediate start so the admin doesn't have to log off/on now;
    only works if the installer is running under Account's own session. }
  RunAdminCommand('{sys}\schtasks.exe', '/run /tn ' + QuoteArg(TaskName),
    'Не удалось сразу запустить задачу Worker (запустится при следующем входе)');
end;

{ Reinstall-over-existing support: sc.exe create fails if the service already
  exists, and a running Server/Worker exe keeps its own file locked so [Files]
  can't overwrite it. Tear down the previous registration first — same
  stop/delete/end commands as [UninstallRun], just quiet (missing service or
  task here is the normal first-install case, not an error worth a MsgBox).
  ponytail: fixed 2s wait for the SCM to actually release the exe handle after
  "sc stop" returns — no polling; bump the delay or poll SERVICE_STOPPED if a
  slower machine still hits a file-in-use error during copy. }
procedure PrepareReinstall;
var
  ResultCode: Integer;
begin
  if WizardIsComponentSelected('server') then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServerSvc}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServerSvc}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if WizardIsComponentSelected('worker') then
  begin
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/end /tn "{#WorkerSvc}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "{#WorkerSvc}" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Account, Password: String;
begin
  if CurStep = ssInstall then
  begin
    PrepareReinstall;
    exit;
  end;
  if CurStep <> ssPostInstall then exit;

  Account := AccountPage.Values[0];
  Password := AccountPage.Values[1];

  if WizardIsComponentSelected('server') then
  begin
    PatchJsonKey(ExpandConstant('{app}\Server\appsettings.Local.json'), 'RootPath', ResolvedPath, True);
    PatchJsonKey(ExpandConstant('{app}\Server\appsettings.Local.json'), 'Token', TelegramPage.Values[0], True);
    GrantServiceLogonRight(Account);
    if RegisterService('{#ServerSvc}', 'TelegramBot Server', ExpandConstant('{app}\Server\{#ServerExe}'), Account, Password) then
      GrantAccess(ExpandConstant('{app}'), Account);
  end;

  if WizardIsComponentSelected('worker') then
  begin
    WriteWorkerRootPath(ExpandConstant('{app}\Worker\appsettings.Local.json'), ResolvedPath);
    if RegisterWorkerTask('{#WorkerSvc}', ExpandConstant('{app}\Worker\{#WorkerExe}'), Account) then
      GrantAccess(ExpandConstant('{app}'), Account);
  end;

  { Grant network share access on the resolved UNC path itself (best-effort —
    requires rights on the file server, may need a domain admin to do this
    step separately if the installer's admin isn't also a share admin). }
  if WizardIsComponentSelected('server') or WizardIsComponentSelected('worker') then
    GrantAccess(ResolvedPath, Account);
end;

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServerSvc}"; Flags: runhidden; Components: server; RunOnceId: "StopServer"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServerSvc}"; Flags: runhidden; Components: server; RunOnceId: "DeleteServer"
Filename: "{sys}\schtasks.exe"; Parameters: "/end /tn ""{#WorkerSvc}"""; Flags: runhidden; Components: worker; RunOnceId: "EndWorkerTask"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""{#WorkerSvc}"" /f"; Flags: runhidden; Components: worker; RunOnceId: "DeleteWorkerTask"

[UninstallDelete]
; [Files] tracks only what was shipped in publish\ — appsettings.Local.json for
; Worker (and any runtime logs) are written directly by CurStepChanged/the app
; itself, so Inno's uninstaller doesn't know about them and leaves the folder
; non-empty. Delete the whole tree explicitly instead.
Type: filesandordirs; Name: "{app}"
