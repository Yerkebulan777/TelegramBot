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
function WNetGetConnectionA(lpLocalName: String; lpRemoteName: String; var cbRemoteName: DWORD): Longint;
  external 'WNetGetConnectionA@mpr.dll stdcall';

var
  AccountPage: TInputQueryWizardPage;
  PathPage: TInputDirWizardPage;
  TelegramPage: TInputQueryWizardPage;
  ResolvedPath: String;

{ Resolves "B:" to "\\server\share" using the CURRENT session's drive map.
  ponytail: WNetGetConnectionA via Pascal Script FFI is a known-finicky idiom
  (buffer marshalling varies by Inno version) — falls back to raw input
  unresolved, so a typo'd/already-UNC path still installs, just unresolved. }
function ResolveDriveToUNC(const Input: String): String;
var
  Buffer: String;
  BufferSize: DWORD;
  NullPos: Integer;
begin
  Result := Trim(Input);
  if (Length(Result) = 2) and (Result[2] = ':') then
  begin
    Buffer := StringOfChar(' ', 260);
    BufferSize := 260;
    if WNetGetConnectionA(Result, Buffer, BufferSize) = 0 then
    begin
      NullPos := Pos(#0, Buffer);
      if NullPos > 1 then
        Result := Copy(Buffer, 1, NullPos - 1)
      else if NullPos = 0 then
        Result := Trim(Buffer);
    end
    else
      MsgBox('Не удалось определить сетевой путь для диска ' + Result + '. ' +
        'Проверьте, что диск подключён (net use), либо введите UNC-путь напрямую (\\сервер\шара).',
        mbError, MB_OK);
  end;
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
    'Настройки Telegram-бота', 'Только для Server — токен бота и Telegram ID администратора.',
    'Токен выдаёт @BotFather. AdminUserId — числовой Telegram ID (узнать можно у @userinfobot).');
  TelegramPage.Add('Bot token:', False);
  TelegramPage.Add('Admin Telegram ID:', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = TelegramPage.ID then
    Result := not WizardIsComponentSelected('server');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
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
    ResolvedPath := ResolveDriveToUNC(PathPage.Values[0]);
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
      exit;
    end;
    if StrToIntDef(Trim(TelegramPage.Values[1]), -1) <= 0 then
    begin
      MsgBox('Admin Telegram ID должен быть положительным числом.', mbError, MB_OK);
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
    SvcName + ' failure reset= 86400 actions= restart/5000/restart/10000/restart/30000',
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
  CreateCmd := Format('/create /tn %s /tr %s /sc onlogon /ru %s /it /rl highest /f', [
    QuoteSc(TaskName), QuoteSc(ExePath), QuoteSc(Account)]);
  Result := RunAdminCommand('{sys}\schtasks.exe', CreateCmd,
    'Не удалось создать задачу планировщика для Worker');
  if not Result then exit;
  { Best-effort immediate start so the admin doesn't have to log off/on now;
    only works if the installer is running under Account's own session. }
  RunAdminCommand('{sys}\schtasks.exe', '/run /tn ' + QuoteSc(TaskName),
    'Не удалось сразу запустить задачу Worker (запустится при следующем входе)');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Account, Password: String;
begin
  if CurStep <> ssPostInstall then exit;

  Account := AccountPage.Values[0];
  Password := AccountPage.Values[1];

  if WizardIsComponentSelected('server') then
  begin
    PatchJsonKey(ExpandConstant('{app}\Server\appsettings.Local.json'), 'RootPath', ResolvedPath, True);
    PatchJsonKey(ExpandConstant('{app}\Server\appsettings.Local.json'), 'Token', TelegramPage.Values[0], True);
    PatchJsonKey(ExpandConstant('{app}\Server\appsettings.Local.json'), 'AdminUserId', Trim(TelegramPage.Values[1]), False);
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
