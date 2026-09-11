; TelegramBot installer — Server and Worker as interactive Task Scheduler
; jobs (onlogon), not Windows Services. Why: Docs/ADR.md ADR-012.
;
; Prerequisite (run before compiling this script):
;   dotnet publish TelegramBot.Server\TelegramBot.Server.csproj -c Release -o Installer\publish\Server
;   dotnet publish TelegramBot.Worker\TelegramBot.Worker.csproj -c Release -o Installer\publish\Worker
;
; XML import (not schtasks /create switches) so we can set WorkingDirectory
; (appsettings.Local.json next to the exe), ExecutionTimeLimit=PT0S (the
; 72 h default would kill a long-running bot), and RestartOnFailure.

#define AppName "TelegramBot"
#define ServerExe "TelegramBot.Server.exe"
#define WorkerExe "TelegramBot.Worker.exe"
#define RootPathSetupExe "TelegramBot.RootPathSetup.exe"
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
Source: "publish\RootPathSetup\*"; DestDir: "{app}\RootPathSetup"; Components: server; Flags: recursesubdirs ignoreversion
Source: "publish\PostgresConnectionCheck\*"; DestDir: "{app}\Tools\PostgresConnectionCheck"; Flags: recursesubdirs ignoreversion
Source: "..\docker-compose.yml"; DestDir: "{commonappdata}\TelegramBot\PostgreSQL"; Components: server; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\TelegramBot\Изменить рабочую папку"; Filename: "{app}\RootPathSetup\{#RootPathSetupExe}"; Components: server

[Code]
var
  AccountPage: TInputQueryWizardPage;
  TelegramPage: TInputQueryWizardPage;
  MustRestartForLegacyService: Boolean;

procedure InitializeWizard;
begin
  AccountPage := CreateInputQueryPage(wpSelectComponents,
    'Учётная запись', 'Под какой учёткой будут работать Server/Worker?',
    'Не используйте LocalSystem/NetworkService — им нужен явный доступ к сетевой шаре. ' +
    'По умолчанию подставлена текущая учётка (у неё уже есть доступ к сетевой шаре в этой сессии). ' +
    'Оба процесса — задачи планировщика при входе в систему (интерактивная сессия, не служба). ' +
    'Машина должна оставаться залогиненной под этой учёткой (на выделенном ПК — автологон), ' +
    'иначе после перезагрузки Server и Worker не стартуют.');
  AccountPage.Add('Имя учётной записи:', False);
  AccountPage.Values[0] := ExpandConstant('{%USERDOMAIN}\{username}');

  TelegramPage := CreateInputQueryPage(AccountPage.ID,
    'Настройки Telegram-бота', 'Только для Server — токен бота.',
    'Токен выдаёт @BotFather.');
  TelegramPage.Add('Bot token:', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = AccountPage.ID then
    Result := not WizardIsComponentSelected('server') and not WizardIsComponentSelected('worker')
  else if PageID = TelegramPage.ID then
    Result := not WizardIsComponentSelected('server');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = AccountPage.ID then
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

{ На чистой установке создаёт Local.json с токеном. При повторной
  установке точечно обновляет токен, не перезаписывая остальные настройки. }
procedure PatchJsonKey(const JsonPath, KeyName, NewValue: String);
var
  Lines: TArrayOfString;
  I: Integer;
  Suffix, TrimmedLine: String;
begin
  if not LoadStringsFromFile(JsonPath, Lines) then
  begin
    if not SaveStringToFile(JsonPath,
      '{' + #13#10 +
      '  "TelegramBot": {' + #13#10 +
      '    "' + KeyName + '": "' + JsonEscape(NewValue) + '"' + #13#10 +
      '  }' + #13#10 +
      '}' + #13#10, False) then
      MsgBox('Не удалось создать файл: ' + JsonPath, mbError, MB_OK);
    exit;
  end;

  for I := 0 to GetArrayLength(Lines) - 1 do
    if Pos('"' + KeyName + '"', Lines[I]) > 0 then
    begin
      TrimmedLine := TrimRight(Lines[I]);
      if (Length(TrimmedLine) > 0) and (TrimmedLine[Length(TrimmedLine)] = ',') then
        Suffix := ','
      else
        Suffix := '';
      Lines[I] := '    "' + KeyName + '": "' + JsonEscape(NewValue) + '"' + Suffix;
      SaveStringsToFile(JsonPath, Lines, False);
      exit;
    end;

  MsgBox('Не найдена строка "' + KeyName + '" в ' + JsonPath + '.', mbError, MB_OK);
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
  (sc.exe leftover teardown, schtasks.exe): run it through cmd.exe with
  stdout+stderr redirected to a log file, and on failure show that log
  alongside the exit code. A bare "код 1" says nothing about *why* a
  command failed. cmd.exe's /c quoting quirk: when the argument starts and
  ends with a quote, cmd strips exactly that outer pair before parsing. }
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
  { schtasks writes OEM bytes; an ANSI cast garbles localized errors. }
  if Exe = '{sys}\schtasks.exe' then
    OemToCharBuff(LogTextA);
  LogText := Trim(String(LogTextA));

  if LogText <> '' then
    MsgBox(ErrorContext + ' (код ' + IntToStr(ResultCode) + '):' + #13#10#13#10 + LogText, mbError, MB_OK)
  else
    MsgBox(ErrorContext + ' (код ' + IntToStr(ResultCode) + ').', mbError, MB_OK);
end;

function XmlEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '&', '&amp;', True);
  StringChangeEx(Result, '<', '&lt;', True);
  StringChangeEx(Result, '>', '&gt;', True);
  StringChangeEx(Result, '"', '&quot;', True);
end;

{ Logon-trigger interactive task. XML (not schtasks /create switches):
  WorkingDirectory next to the exe, ExecutionTimeLimit=PT0S (no 72 h
  default), RestartOnFailure. Delay PT5S after logon so desktop/network
  can settle (auto-logon on dedicated machines). }
function RegisterLogonTask(const TaskName, ExePath, Account: String): Boolean;
var
  XmlPath, Xml, WorkDir: String;
  XmlBytes: AnsiString;
  I, CodeUnit: Integer;
begin
  WorkDir := ExtractFilePath(ExePath);
  Xml :=
    '<?xml version="1.0" encoding="UTF-16"?>' + #13#10 +
    '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' + #13#10 +
    '  <Triggers>' + #13#10 +
    '    <LogonTrigger>' + #13#10 +
    '      <Enabled>true</Enabled>' + #13#10 +
    '      <UserId>' + XmlEscape(Account) + '</UserId>' + #13#10 +
    '      <Delay>PT5S</Delay>' + #13#10 +
    '    </LogonTrigger>' + #13#10 +
    '  </Triggers>' + #13#10 +
    '  <Principals>' + #13#10 +
    '    <Principal id="Author">' + #13#10 +
    '      <UserId>' + XmlEscape(Account) + '</UserId>' + #13#10 +
    '      <LogonType>InteractiveToken</LogonType>' + #13#10 +
    '      <RunLevel>HighestAvailable</RunLevel>' + #13#10 +
    '    </Principal>' + #13#10 +
    '  </Principals>' + #13#10 +
    '  <Settings>' + #13#10 +
    '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>' + #13#10 +
    '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' + #13#10 +
    '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>' + #13#10 +
    '    <AllowHardTerminate>true</AllowHardTerminate>' + #13#10 +
    '    <StartWhenAvailable>true</StartWhenAvailable>' + #13#10 +
    '    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>' + #13#10 +
    '    <AllowStartOnDemand>true</AllowStartOnDemand>' + #13#10 +
    '    <Enabled>true</Enabled>' + #13#10 +
    '    <Hidden>false</Hidden>' + #13#10 +
    '    <RunOnlyIfIdle>false</RunOnlyIfIdle>' + #13#10 +
    '    <WakeToRun>false</WakeToRun>' + #13#10 +
    '    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>' + #13#10 +
    '    <Priority>7</Priority>' + #13#10 +
    '    <RestartOnFailure>' + #13#10 +
    '      <Interval>PT1M</Interval>' + #13#10 +
    '      <Count>3</Count>' + #13#10 +
    '    </RestartOnFailure>' + #13#10 +
    '  </Settings>' + #13#10 +
    '  <Actions Context="Author">' + #13#10 +
    '    <Exec>' + #13#10 +
    '      <Command>' + XmlEscape(ExePath) + '</Command>' + #13#10 +
    '      <WorkingDirectory>' + XmlEscape(WorkDir) + '</WorkingDirectory>' + #13#10 +
    '    </Exec>' + #13#10 +
    '  </Actions>' + #13#10 +
    '</Task>' + #13#10;
  XmlPath := ExpandConstant('{tmp}\') + TaskName + '.xml';
  { schtasks /xml expects UTF-16LE. SaveStringToFile writes raw bytes,
    so include the BOM and preserve each Unicode code unit explicitly. }
  SetLength(XmlBytes, 2 + Length(Xml) * 2);
  XmlBytes[1] := Chr(255);
  XmlBytes[2] := Chr(254);
  for I := 1 to Length(Xml) do
  begin
    CodeUnit := Ord(Xml[I]);
    XmlBytes[I * 2 + 1] := Chr(CodeUnit and $FF);
    XmlBytes[I * 2 + 2] := Chr(CodeUnit shr 8);
  end;
  if not SaveStringToFile(XmlPath, XmlBytes, False) then
  begin
    MsgBox('Не удалось записать XML задачи ' + TaskName, mbError, MB_OK);
    Result := False;
    exit;
  end;
  Result := RunAdminCommand('{sys}\schtasks.exe',
    Format('/create /tn %s /xml %s /f', [QuoteArg(TaskName), QuoteArg(XmlPath)]),
    'Не удалось создать задачу планировщика ' + TaskName);
  if not Result then exit;
  { Best-effort immediate start so the admin doesn't have to log off/on now;
    only works if the installer is running under Account's own session. }
  RunAdminCommand('{sys}\schtasks.exe', '/run /tn ' + QuoteArg(TaskName),
    'Не удалось сразу запустить задачу ' + TaskName + ' (запустится при следующем входе)');
end;

// If Server is selected and localhost:5432 is empty, the helper starts
// PostgreSQL 18 in the already running Docker Desktop (`docker compose up -d --wait`)
// under {commonappdata}\TelegramBot\PostgreSQL and patches ConnectionStrings
// in appsettings.Local.json. Docker Desktop must already be installed and
// running. Worker-only never starts a second cluster.
procedure EnsurePostgres;
var
  Args: String;
begin
  WizardForm.StatusLabel.Caption := 'Настройка PostgreSQL в Docker Desktop...';
  Args := 'ensure';
  if WizardIsComponentSelected('server') then
    Args := Args + ' --server ' + QuoteArg(ExpandConstant('{app}\Server'));
  if WizardIsComponentSelected('worker') then
    Args := Args + ' --worker ' + QuoteArg(ExpandConstant('{app}\Worker'));
  if not RunAdminCommand(
    '{app}\Tools\PostgresConnectionCheck\PostgresConnectionCheck.exe',
    Args,
    'Не удалось настроить PostgreSQL в Docker Desktop') then
    RaiseException('Установка остановлена: PostgreSQL не настроен. Запустите Docker Desktop и повторите Setup.');
end;

{ Tear down previous registration so [Files] can overwrite locked exes.
  Quiet: missing service/task is a first install. After sc stop wait 2s for
  the SCM to release the exe handle (no polling). }
procedure PrepareReinstall;
var
  ResultCode: Integer;
begin
  if WizardIsComponentSelected('server') then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServerSvc}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode = 0 then
    begin
      MustRestartForLegacyService := True;
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServerSvc}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(2000);
      Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServerSvc}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/end /tn "{#ServerSvc}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "{#ServerSvc}" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if WizardIsComponentSelected('worker') then
  begin
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/end /tn "{#WorkerSvc}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "{#WorkerSvc}" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Account: String;
begin
  if CurStep = ssInstall then
  begin
    PrepareReinstall;
    exit;
  end;
  if CurStep <> ssPostInstall then exit;

  Account := AccountPage.Values[0];

  if WizardIsComponentSelected('server') then
    PatchJsonKey(ExpandConstant('{app}\Server\appsettings.Local.json'), 'Token', TelegramPage.Values[0]);

  if WizardIsComponentSelected('server') or WizardIsComponentSelected('worker') then
    EnsurePostgres;

  if WizardIsComponentSelected('server') then
    RegisterLogonTask('{#ServerSvc}', ExpandConstant('{app}\Server\{#ServerExe}'), Account);

  if WizardIsComponentSelected('worker') then
    RegisterLogonTask('{#WorkerSvc}', ExpandConstant('{app}\Worker\{#WorkerExe}'), Account);

  if WizardIsComponentSelected('server') or WizardIsComponentSelected('worker') then
    GrantAccess(ExpandConstant('{app}'), Account);
end;

function NeedRestart(): Boolean;
begin
  { Reboot only if an old Win32 service was still registered — sc delete can
    leave it marked for deletion until reboot. Fresh task-only installs don't
    need a restart; the logon task is /run immediately. }
  Result := MustRestartForLegacyService;
end;

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/end /tn ""{#ServerSvc}"""; Flags: runhidden; Components: server; RunOnceId: "EndServerTask"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""{#ServerSvc}"" /f"; Flags: runhidden; Components: server; RunOnceId: "DeleteServerTask"
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServerSvc}"; Flags: runhidden; Components: server; RunOnceId: "StopLegacyServer"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServerSvc}"; Flags: runhidden; Components: server; RunOnceId: "DeleteLegacyServer"
Filename: "{sys}\schtasks.exe"; Parameters: "/end /tn ""{#WorkerSvc}"""; Flags: runhidden; Components: worker; RunOnceId: "EndWorkerTask"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""{#WorkerSvc}"" /f"; Flags: runhidden; Components: worker; RunOnceId: "DeleteWorkerTask"

[UninstallDelete]
; [Files] tracks only what was shipped in publish\ — appsettings.Local.json for
; Server (and any runtime logs) are written directly by CurStepChanged/the app
; itself, so Inno's uninstaller doesn't know about them and leaves the folder
; non-empty. Delete the whole tree explicitly instead.
Type: filesandordirs; Name: "{app}"
