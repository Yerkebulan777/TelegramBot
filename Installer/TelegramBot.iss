; TelegramBot installer — Server as a Windows Service, Worker as a Task
; Scheduler job (see below for why Worker can't be a plain service).
;
; Prerequisite (run before compiling this script):
;   dotnet publish TelegramBot.Server\TelegramBot.Server.csproj -c Release -o Installer\publish\Server
;   dotnet publish TelegramBot.Worker\TelegramBot.Worker.csproj -c Release -o Installer\publish\Worker
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

[Code]
var
  AccountPage: TInputQueryWizardPage;
  PathPage: TInputDirWizardPage;
  TelegramPage: TInputQueryWizardPage;
  ResolvedPath: String;

{ Resolves "B:" (or "B:\", which is what CreateInputDirPage actually hands
  back for a drive root — it normalizes with a trailing backslash) to
  "\\server\share" using the CURRENT session's drive map. ExpandUNCFileName
  is a built-in Pascal Script function that does this natively — no manual
  WNetGetConnection FFI (that was the actual bug: Inno 6 Pascal strings are
  Unicode, but WNetGetConnectionA is the ANSI entry point, so the buffer
  marshalling silently failed and every install kept the raw "B:\", which is
  invisible to a Windows service — see chat).
  Success is False when Input was a bare drive letter that ExpandUNCFileName
  couldn't map to a network path (not connected, or a genuinely local disk);
  the caller must block on that — an unresolved drive letter is invisible to
  a Windows service and is exactly what caused the Server outage this was
  written to fix, so this can no longer be a soft warning that lets Next
  through. An input that's already a UNC path always succeeds untouched. }
function ResolveDriveToUNC(const Input: String; var Success: Boolean): String;
var
  DriveSpec: String;
begin
  Result := ExpandUNCFileName(Trim(Input));
  DriveSpec := Trim(Input);
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

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResolveOk: Boolean;
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
      MsgBox('Не удалось определить сетевой путь для диска ' + Trim(PathPage.Values[0]) + '. ' +
        'Проверьте, что диск подключён (net use), либо введите UNC-путь напрямую (\\сервер\шара).',
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

function QuoteSc(const S: String): String;
begin
  Result := '"' + S + '"';
end;

procedure GrantAccess(const Path, Account: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\icacls.exe'),
    Format('%s /grant %s:(OI)(CI)F', [QuoteSc(Path), QuoteSc(Account)]),
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Shared mechanic: run a privileged CLI command, report exit code on failure.
  Both sc.exe and schtasks.exe registration follow this exact shape — only
  the command line and the domain-specific failure message differ. }
function RunAdminCommand(const Exe, Args, ErrorContext: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant(Exe), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    MsgBox(ErrorContext + ' (код ' + IntToStr(ResultCode) + ').', mbError, MB_OK);
end;

procedure InsertArrayLine(var Lines: TArrayOfString; Index: Integer; const NewLine: String);
var
  I, Len: Integer;
begin
  Len := GetArrayLength(Lines);
  SetArrayLength(Lines, Len + 1);
  for I := Len downto Index + 1 do
    Lines[I] := Lines[I - 1];
  Lines[Index] := NewLine;
end;

{ Grants "Log on as a service" (SeServiceLogonRight) to Account via secedit,
  merging it into whatever the policy already lists instead of overwriting
  it — secedit /configure with /areas USER_RIGHTS only touches the rights
  present in the .cfg it's given, so exporting current state, appending
  Account to the existing SeServiceLogonRight line, and reapplying leaves
  every other right untouched. This is what the Services GUI does silently
  via LsaAddAccountRights when you set a service's logon account by hand;
  sc.exe create skips that step entirely, which is the actual reason a fresh
  install otherwise needs a manual secpol.msc visit.
  Best-effort only: if a domain GPO enforces this right, it overwrites the
  local grant again on its own refresh cycle — no local fix survives that,
  see README troubleshooting section. }
procedure GrantServiceLogonRight(const Account: String);
var
  CfgPath, DbPath, LogPath: String;
  Lines: TArrayOfString;
  I: Integer;
  KeyFound, SectionFound: Boolean;
begin
  CfgPath := ExpandConstant('{tmp}\secpol_export.cfg');
  DbPath := ExpandConstant('{tmp}\secpol_apply.sdb');
  LogPath := ExpandConstant('{tmp}\secpol_apply.log');

  if not RunAdminCommand('{sys}\secedit.exe',
    Format('/export /cfg "%s" /areas USER_RIGHTS', [CfgPath]),
    'Не удалось выгрузить текущую политику "Вход в качестве службы"') then
    exit;

  if not LoadStringsFromFile(CfgPath, Lines) then
  begin
    MsgBox('Не удалось прочитать выгруженную политику: ' + CfgPath, mbError, MB_OK);
    exit;
  end;

  { secedit exports as UTF-16 (declared by "Unicode=yes" under [Unicode]), but
    SaveStringsToFile below only writes ANSI — there's no Unicode-writing
    counterpart in Pascal Script. Left as "yes", the re-saved ANSI file would
    still claim to be UTF-16 and secedit /configure would fail to parse it.
    Flip the flag to match what we actually write; safe here since every
    value involved (account name, key names) is plain ASCII. }
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Trim(Lines[I]) = 'Unicode=yes' then
      Lines[I] := 'Unicode=no';

  KeyFound := False;
  SectionFound := False;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Pos('SeServiceLogonRight', Lines[I]) = 1 then
    begin
      if Pos(Account, Lines[I]) = 0 then
        Lines[I] := TrimRight(Lines[I]) + ',' + Account;
      KeyFound := True;
      break;
    end;
    if Trim(Lines[I]) = '[Privilege Rights]' then
      SectionFound := True;
  end;

  if not KeyFound then
  begin
    if SectionFound then
    begin
      for I := 0 to GetArrayLength(Lines) - 1 do
      begin
        if Trim(Lines[I]) = '[Privilege Rights]' then
        begin
          InsertArrayLine(Lines, I + 1, 'SeServiceLogonRight = ' + Account);
          break;
        end;
      end;
    end
    else
    begin
      { No local Privilege Rights at all — typical when a domain GPO owns
        User Rights Assignment for this machine and the local security
        database has nothing of its own to export. secedit still accepts a
        template that introduces a brand-new section, so append one. }
      InsertArrayLine(Lines, GetArrayLength(Lines), '[Privilege Rights]');
      InsertArrayLine(Lines, GetArrayLength(Lines), 'SeServiceLogonRight = ' + Account);
    end;
  end;

  SaveStringsToFile(CfgPath, Lines, False);

  RunAdminCommand('{sys}\secedit.exe',
    Format('/configure /db "%s" /cfg "%s" /areas USER_RIGHTS /log "%s"', [DbPath, CfgPath, LogPath]),
    'Не удалось применить право "Вход в качестве службы". Подробности в логе: ' + LogPath);
end;

function RegisterService(const SvcName, DisplayName, ExePath, Account, Password: String): Boolean;
var
  CreateCmd: String;
begin
  CreateCmd := Format('create %s binPath= %s obj= %s password= %s start= delayed-auto DisplayName= %s', [
    SvcName, QuoteSc(ExePath), QuoteSc(Account), QuoteSc(Password), QuoteSc(DisplayName)]);
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
    QuoteSc(TaskName), ExePath, QuoteSc(Account)]);
  Result := RunAdminCommand('{sys}\schtasks.exe', CreateCmd,
    'Не удалось создать задачу планировщика для Worker');
  if not Result then exit;
  { Best-effort immediate start so the admin doesn't have to log off/on now;
    only works if the installer is running under Account's own session. }
  RunAdminCommand('{sys}\schtasks.exe', '/run /tn ' + QuoteSc(TaskName),
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
