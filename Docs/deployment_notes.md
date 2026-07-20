# Деплой как Windows Service

Server и Worker — два процесса, две отдельные службы. Обе уже используют
`Host.UseWindowsService(...)` ([Server/Program.cs](../TelegramBot.Server/Program.cs),
[Worker/Program.cs](../TelegramBot.Worker/Program.cs)) — при запуске под SCM
хост сам переключается в режим службы, при обычном `dotnet run` работает как консоль.

## 1. Publish

```powershell
dotnet publish TelegramBot.Server\TelegramBot.Server.csproj -c Release -o C:\Services\TelegramBotServer
dotnet publish TelegramBot.Worker\TelegramBot.Worker.csproj -c Release -o C:\Services\TelegramBotWorker
```

Положить `appsettings.Local.json` рядом с exe в каждой папке (см. [README.md](../README.md#локальная-конфигурация)).

## 2. Service account

**Не LocalSystem.** LocalSystem работает под identity `PC$` — без явных прав на
сетевую шару доступ к `\\server\share` будет отклонён. NetworkService не лучше —
тоже зависит от прав, выданных именно этой системной учётке на удалённой машине.

1. Создать выделенную локальную/доменную учётку, например `svc_telegram_bot`.
2. Выдать пароль без истечения срока.
3. `secpol.msc` → Local Policies → User Rights Assignment → **Log on as a service** → добавить `svc_telegram_bot`.
4. Выдать NTFS/share права на всё, что нужно приложению: `FileSystem:RootPath`,
   `FileSystem:TaskDirectory`, лог-папки, сетевые шары с Revit-файлами:

   ```powershell
   icacls "\\server\share\path" /grant svc_telegram_bot:(OI)(CI)F
   ```

5. В коде и конфиге — только UNC-пути (`\\server\share\...`). Смонтированные
   буквы дисков (`Z:\...`) служба не видит — они существуют в пользовательской сессии.

## 3. Регистрация служб

Из PowerShell/cmd с правами администратора:

```powershell
sc.exe create TelegramBotServer binPath= "C:\Services\TelegramBotServer\TelegramBot.Server.exe" obj= ".\svc_telegram_bot" password= "ПАРОЛЬ" start= delayed-auto
sc.exe create TelegramBotWorker binPath= "C:\Services\TelegramBotWorker\TelegramBot.Worker.exe" obj= ".\svc_telegram_bot" password= "ПАРОЛЬ" start= delayed-auto

sc.exe failure TelegramBotServer reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe failure TelegramBotWorker reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

`start= delayed-auto` — старт после системных служб (сеть, PostgreSQL успевают подняться).
`sc.exe failure ... actions= restart/...` — авто-рестарт при краше; Worker уже
выставляет `Environment.ExitCode = 1` при фатальной ошибке ([Worker/Program.cs](../TelegramBot.Worker/Program.cs)),
без этого SCM считал бы падение штатным завершением и не перезапускал.

Пробелы после `=` в `sc.exe` обязательны — без них команда молча падает.

## 4. Запуск и проверка

```powershell
Start-Service TelegramBotServer
Start-Service TelegramBotWorker
Get-Service TelegramBotServer, TelegramBotWorker
```

| Тест | Действие | Ожидаемый результат |
|---|---|---|
| Локальный доступ | Запустить exe вручную под текущим пользователем, обратиться к `\\server\share` | OK |
| Служба, не тот account | Служба от `LocalSystem`/`NetworkService` | Скорее всего отказ доступа к сети |
| Служба, целевой account | Служба от `svc_telegram_bot` по UNC-пути | OK |

Логи: Event Viewer → Windows Logs → Application, плюс Serilog rolling files
(`%USERPROFILE%\...\Logs\Server\`, `\Worker\`) — но под service-account это
профиль `svc_telegram_bot`, не текущего пользователя.

## Удаление / обновление

```powershell
Stop-Service TelegramBotServer, TelegramBotWorker
sc.exe delete TelegramBotServer
sc.exe delete TelegramBotWorker
```

Для обновления — `Stop-Service`, заменить файлы в `C:\Services\...`, `Start-Service`.
