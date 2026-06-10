# Code Facts — TelegramBot Repository

**Source of truth:** direct read of files on disk in
`C:\Users\y.zhumabayev\Repository\TelegramBot`. No assumptions, no
interpretation. Every section lists concrete file paths, namespaces,
classes, package versions.

> Note: this is the *actual state of the code*, not what AGENTS.md / README.md / ROADMAP.md claim.
> Where the two diverge, both are noted in the relevant section.

---

## 1. Структура проектов (фактическая)

### 1.1 `TelegramBot.Core/`

```
TelegramBot.Core/
├── Config/                       (5 .cs файлов)
│   ├── BotOptions.cs
│   ├── CommandConfig.cs
│   ├── FileSystemOptions.cs
│   ├── RateLimitOptions.cs
│   └── WorkerOptions.cs
├── Constants/                    (6 .cs файлов)
│   ├── ButtonTexts.cs
│   ├── CallbackPrefixes.cs        ← реальные префиксы (в Models/ лежит ПУСТАЯ дублирующая заглушка)
│   ├── CommandCodes.cs
│   ├── CommandPriorities.cs
│   ├── CommandStatuses.cs         ← [Obsolete] алиас на Statuses
│   └── Statuses.cs                ← актуальный набор статусов
├── DTOs/                         (2 .cs файла)
│   ├── CallbackQueryDto.cs
│   └── MessageDto.cs
├── Extensions/                   (пустая)
├── Interfaces/                   (11 интерфейсов)
│   ├── ICallbackDispatcher.cs
│   ├── ICallbackHandler.cs
│   ├── ICommandAppService.cs
│   ├── ICommandDataService.cs
│   ├── IDataService.cs
│   ├── IDatabaseInitializer.cs
│   ├── IMessageTrackingDataService.cs
│   ├── INotificationDataService.cs
│   ├── ISessionDataService.cs
│   ├── ISessionManager.cs
│   └── IUserDataService.cs
├── Models/                       (11 .cs файлов)
│   ├── BotUser.cs
│   ├── CallbackContext.cs
│   ├── CallbackPrefixes.cs        ← ПУСТОЙ (только namespace и EOF), реальные константы в Constants/CallbackPrefixes.cs
│   ├── ParsedCallback.cs          ← record struct + static class CallbackDataParser
│   ├── PendingCommand.cs
│   ├── SessionCommands.cs
│   ├── SessionStatus.cs
│   ├── SessionsList.cs
│   ├── UserAccessStatus.cs        ← enum: Pending=0, Approved=1, Rejected=2, Blocked=3
│   ├── UserRole.cs                ← enum: User=0, Admin=1
│   └── UserSession.cs
├── Services/
│   └── RateLimiter.cs
└── TelegramBot.Core.csproj
```

### 1.2 `TelegramBot.Data/`

```
TelegramBot.Data/
├── DatabaseInitializer.cs        (namespace TelegramBot.Data)
├── NpgsqlHelper.cs               (namespace TelegramBot.Data)
├── PostgresDataService.cs        (namespace TelegramBot.Data) — единственный IDataService
├── Sql/                          (5 partial файлов констант SqlQueries.*)
│   ├── Queries.Commands.cs
│   ├── Queries.Schema.cs
│   ├── Queries.Sessions.cs
│   ├── Queries.TrackedMessages.cs
│   └── Queries.Users.cs
└── TelegramBot.Data.csproj
```

### 1.3 `TelegramBot.Server/`

```
TelegramBot.Server/
├── Config/
│   └── BotCommandsSetup.cs
├── Constants/
│   └── HandlerPriorities.cs       ← AccessRequest=0, FileNavigation=10, FileSelection=20, Default=100
├── Extensions/
│   └── DependencyInjectionExtensions.cs
├── Helpers/
│   ├── MarkdownHelper.cs
│   └── SerilogSetup.cs
├── Interfaces/
│   ├── IKeyboardBuilder.cs
│   ├── ISlashCommandService.cs
│   └── ITelegramOutputService.cs
├── Models/
│   └── CommandDefinition.cs       ← содержит enum CommandGroup
├── Program.cs                    (namespace TelegramBot.Server) [SupportedOSPlatform("windows")]
├── Properties/
│   └── launchSettings.json
├── Services/
│   ├── Application/
│   │   ├── CallbackDispatcher.cs
│   │   ├── CommandAppService.cs
│   │   ├── Handlers/             (8 файлов)
│   │   │   ├── AccessRequestHandler.cs
│   │   │   ├── CallbackHandlerBase.cs          ← abstract base
│   │   │   ├── CommandSelectionHandler.cs
│   │   │   ├── CommandToggleHandler.cs
│   │   │   ├── FileNavigationHandler.cs
│   │   │   ├── FileSelectionHandler.cs
│   │   │   ├── HandlerHelpers.cs                ← static class с SendActionsReplyKeyboardAsync
│   │   │   └── SessionManagementHandler.cs
│   │   ├── SessionManager.cs
│   │   └── SlashCommandService.cs
│   └── Infrastructure/
│       ├── FileSystem/
│       │   └── FileSystemBrowser.cs
│       └── Telegram/
│           ├── CommandNotificationService.cs    ← BackgroundService, LISTEN command_completed
│           ├── KeyboardBuilder.cs
│           ├── TelegramBotHostedService.cs      ← BackgroundService, polling
│           ├── TelegramOutputService.cs
│           └── TelegramUpdateMapper.cs
├── appsettings.json              (закоммичен)
├── appsettings.Local.json        (ЗАКОММИЧЕН, содержит реальный Telegram-токен — см. §10)
└── TelegramBot.Server.csproj
```

### 1.4 `TelegramBot.Worker/`

```
TelegramBot.Worker/
├── BimLib/                                  ← каталог ВНУТРИ Worker, НЕ отдельный проект
│   ├── Config/
│   │   └── BimIntegrationOptions.cs
│   ├── Interfaces/
│   │   ├── INavisworksPathResolver.cs
│   │   └── IRevitVersionDetector.cs
│   ├── Models/
│   │   ├── RevitDetectedVersion.cs
│   │   └── RevitProcessHealth.cs           ← + enum RevitProcessStatus { Healthy, NotResponding, Error }
│   ├── Monitor/
│   │   ├── DialogDismisser.cs
│   │   ├── NavisworksProcessTracker.cs
│   │   ├── ProcessHealthHelper.cs          ← internal static
│   │   ├── RevitProcessTracker.cs
│   │   ├── WindowInfo.cs
│   │   └── WindowUtil.cs
│   ├── Native/
│   │   ├── User32.cs                       ← P/Invoke DllImport user32.dll
│   │   └── Win32Types.cs                   ← static class Win32Consts (BM_CLICK и т.п.)
│   └── Services/
│       ├── NavisworksPathResolver.cs
│       ├── RevitPathResolver.cs
│       └── RevitVersionDetector.cs
├── Helpers/
│   └── SerilogSetup.cs
├── Program.cs                              (namespace TelegramBot.Worker) [assembly: SupportedOSPlatform("windows")]
├── Services/
│   ├── BimLibLogFilter.cs
│   └── CommandExecutionService.cs          ← BackgroundService, LISTEN new_tasks
├── appsettings.json                        (закоммичен)
├── appsettings.Local.json                  (ЗАКОММИЧЕН, см. §10)
└── TelegramBot.Worker.csproj               (RootNamespace = TelegramBot.Worker)
```

### 1.5 Дополнительные файлы репозитория

- `TelegramBot.slnx` — корневой solution, перечисляет 4 проекта + папку `Docs/` с тремя файлами.
- `AGENTS.md` (25 972 байт), `README.md` (7 056 байт), `ROADMAP.md` (24 624 байт), `LICENSE.txt` (1 090 байт).
- `docker-compose.yml`, `qodana.yaml`, `.editorconfig` (5 035 байт), `.dockerignore`, `.gitattributes`, `.gitignore`.
- `.github/workflows/ci.yml` (только один workflow).
- `scripts/check-code-style.ps1`, `scripts/db/`.
- `Docs/CommandExecutionAlgorithm.md`, `Docs/execution-algorithm.md`, `Docs/qodana-setup.md`.
- `audit/` (создана мной).

---

## 2. Имена проектов и зависимости (.csproj — полное содержимое)

### 2.1 `TelegramBot.Core.csproj`

| Параметр | Значение |
|----------|----------|
| TargetFramework | `net10.0` |
| Nullable | `enable` |
| ImplicitUsings | `enable` |
| Sdk | `Microsoft.NET.Sdk` |

PackageReference:

- `Microsoft.Extensions.Options` 10.0.8
- `Serilog` 4.3.0
- `Serilog.Sinks.Console` 6.1.1
- `Serilog.Extensions.Hosting` 10.0.0
- `Serilog.Settings.Configuration` 10.0.0
- `Serilog.Sinks.File` 7.0.0

ProjectReference: **нет**.

### 2.2 `TelegramBot.Data.csproj`

| Параметр | Значение |
|----------|----------|
| TargetFramework | `net10.0` |
| Nullable | `enable` |
| ImplicitUsings | `enable` |

PackageReference:

- `Dapper` 2.1.79
- `Npgsql` 10.0.3
- `Npgsql.DependencyInjection` 10.0.3
- `Microsoft.Extensions.Configuration.Abstractions` 10.0.8
- `Microsoft.Extensions.Configuration.Binder` 10.0.8
- `Microsoft.Extensions.Hosting.Abstractions` 10.0.8
- `Microsoft.Extensions.Logging.Abstractions` 10.0.8

ProjectReference: `..\TelegramBot.Core\TelegramBot.Core.csproj`.

### 2.3 `TelegramBot.Server.csproj`

| Параметр | Значение |
|----------|----------|
| TargetFramework | `net10.0` |
| Nullable | `enable` |
| ImplicitUsings | `enable` |
| Sdk | `Microsoft.NET.Sdk.Web` |

PackageReference:

- `Serilog.AspNetCore` 10.0.0
- `Serilog.Settings.Configuration` 10.0.0
- `Serilog.Sinks.Console` 6.1.1
- `Serilog.Sinks.File` 7.0.0
- `Serilog.Sinks.Seq` 9.1.0
- `Telegram.Bot` 22.10.0.1

ProjectReference: `..\TelegramBot.Core\…`, `..\TelegramBot.Data\…`.

### 2.4 `TelegramBot.Worker.csproj`

| Параметр | Значение |
|----------|----------|
| TargetFramework | `net10.0` |
| Nullable | `enable` |
| ImplicitUsings | `enable` |
| Sdk | `Microsoft.NET.Sdk.Worker` |
| RootNamespace | `TelegramBot.Worker` |

PackageReference:

- `Microsoft.Extensions.Hosting` 10.0.8
- `Microsoft.Extensions.Logging.Abstractions` 10.0.8
- `Microsoft.Extensions.Options` 10.0.8
- `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.8
- `OpenMcdf` 3.1.4 ✅ (используется в `RevitVersionDetector.cs`)
- `Serilog.Extensions.Hosting` 10.0.0
- `Serilog.Filters.Expressions` 2.1.0
- `Serilog.Settings.Configuration` 10.0.0
- `Serilog.Sinks.Console` 6.1.1
- `Serilog.Sinks.Seq` 9.0.0
- `Npgsql` 10.0.3
- `Serilog.Sinks.File` 7.0.0

ProjectReference: `..\TelegramBot.Core\…`, `..\TelegramBot.Data\…`.

> **`Microsoft.Win32` и `System.Runtime.Versioning`** не объявлены как PackageReference — это фреймворк-нейтральные API в .NET 10, используются напрямую в `BimLib/Services/RevitPathResolver.cs` и `BimLib/Services/NavisworksPathResolver.cs`.

---

## 3. BimLib — фактическое местоположение

### 3.1 Где лежит

**BimLib — это директория внутри Worker**, **не отдельный проект**:

```
TelegramBot.Worker/BimLib/
```

- `TelegramBot.BimLib.csproj` **не существует** (подтверждено листингом корня).
- Пространства имён — **`TelegramBot.Worker.BimLib.*`**, а **не** `TelegramBot.BimLib.*` как заявлено в AGENTS.md.
- Это вызвано тем, что в `TelegramBot.Worker.csproj` явно задан `<RootNamespace>TelegramBot.Worker</RootNamespace>`; компилятор префиксует `BimLib.*` этим корнем.

> **Расхождение с AGENTS.md:** В AGENTS.md написано
> *«Namespaces: `TelegramBot.BimLib.Config`, `TelegramBot.BimLib.Interfaces`, …»* и
> *«No `TelegramBot.BimLib.csproj` exists»*.
> Реально: namespaces имеют префикс `TelegramBot.Worker.BimLib.*` (см. вывод
> `grep -rn "namespace " BimLib/`). Например, `BimLib/Config/BimIntegrationOptions.cs:1` —
> `namespace TelegramBot.Worker.BimLib.Config;`.

### 3.2 Подпапки BimLib и файлы внутри

| Подпапка | Файлы | Назначение |
|----------|-------|-----------|
| `Config/` | `BimIntegrationOptions.cs` | `MinSupportedVersion=2018`, `MaxSupportedVersion=2026`, `RevitInstallRoot`. `SectionName="BimIntegration"`. |
| `Interfaces/` | `INavisworksPathResolver.cs`, `IRevitVersionDetector.cs` | Всего 2 интерфейса. |
| `Models/` | `RevitDetectedVersion.cs`, `RevitProcessHealth.cs` | + `enum RevitProcessStatus { Healthy, NotResponding, Error }` (объявлен в `RevitProcessHealth.cs`). |
| `Monitor/` | `DialogDismisser.cs`, `NavisworksProcessTracker.cs`, `ProcessHealthHelper.cs`, `RevitProcessTracker.cs`, `WindowInfo.cs`, `WindowUtil.cs` | Мониторинг процессов и окон. |
| `Native/` | `User32.cs`, `Win32Types.cs` (содержит `static class Win32Consts`) | P/Invoke на `user32.dll` + константы. |
| `Services/` | `NavisworksPathResolver.cs`, `RevitPathResolver.cs`, `RevitVersionDetector.cs` | Реализации резолверов путей и детектора версии. |

### 3.3 Атрибут `[SupportedOSPlatform("windows")]`

Присутствует в **трёх** файлах сервисов:

- `BimLib/Services/RevitVersionDetector.cs:18`
- `BimLib/Services/RevitPathResolver.cs:11`
- `BimLib/Services/NavisworksPathResolver.cs:13`

А **также в** `TelegramBot.Worker/Program.cs:13` — `[assembly: SupportedOSPlatform("windows")]`.

В `TelegramBot.Server/Program.cs:14` — `[SupportedOSPlatform("windows")]` (на классе `Program`) + runtime-проверка `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`.

### 3.4 Зависимости BimLib

| Зависимость | Где используется | Откуда приходит |
|-------------|------------------|-----------------|
| `OpenMcdf` (`RootStorage.OpenRead`, `OpenStream`, `Stream.Read`) | `BimLib/Services/RevitVersionDetector.cs:1,134-138` | PackageReference в Worker.csproj v3.1.4 |
| `Microsoft.Win32` (`Registry.LocalMachine.OpenSubKey`) | `BimLib/Services/RevitPathResolver.cs:2,99` и `NavisworksPathResolver.cs:2,125` | встроен в .NET (TargetFramework=net10.0) |
| `System.Diagnostics.Process` | `BimLib/Monitor/RevitProcessTracker.cs:1,17`, `NavisworksProcessTracker.cs:1,23` | встроен в .NET |
| `user32.dll` (P/Invoke) | `BimLib/Native/User32.cs` (`GetWindowText`, `EnumWindows`, `SendMessage` и т.д.) | Windows-системная библиотека |

> **Замечание:** в `TelegramBot.Worker/Program.cs:57` есть комментарий
> `// Отдельный файл для BIM-специфичных логов (Revit, Navisworks — TelegramBot.BimLib.*)`.
> Реальный namespace — `TelegramBot.Worker.BimLib.*` (см. §3.1). Это та же doc-vs-code
> неточность, что и в AGENTS.md.

---

## 4. Реальные namespaces и публичные типы

### 4.1 `TelegramBot.Core.*`

| Namespace | Файл | Публичные типы |
|-----------|------|----------------|
| `TelegramBot.Core.Config` | `BotOptions.cs` | `class BotOptions` (`Token`, `AdminUserIds:long[]`, `SectionName="TelegramBot"`) |
| `TelegramBot.Core.Config` | `CommandConfig.cs` | `class CommandConfig` (`ExecutablePath`, `ArgumentsTemplate`, `AllowedExtensions`, `WorkingDirectory`) |
| `TelegramBot.Core.Config` | `FileSystemOptions.cs` | `class FileSystemOptions` (`required RootPath`, `RvtDirectoryName="01_RVT"`, `ProjectDirectoryName="01_PROJECT"`, `RevitFileExtension=".rvt"`, `SectionFolderPattern=@"^(\d{2}|\d{3}|I{1,3})_"`, `IsAtProjectLevel()`, `IsPathWithinRoot()`) |
| `TelegramBot.Core.Config` | `RateLimitOptions.cs` | `class RateLimitOptions` (`MaxRequests=10`, `WindowSeconds=60`, `MaxFilesPerUserPerDay=1000`, `SectionName="RateLimit"`) |
| `TelegramBot.Core.Config` | `WorkerOptions.cs` | `class WorkerOptions` (`ProcessTimeoutSeconds=10800`, `MaxRetries=5`, `RetryDelayBaseSeconds=60`, `CompletedSessionRetentionDays=30`, `Partitions`, `Commands`) |
| `TelegramBot.Core.Constants` | `ButtonTexts.cs` | `static class ButtonTexts` (`Apply="✅ Применить"`, `Confirm="✅ Подтвердить"`, `Cancel="❌ Отмена"`) |
| `TelegramBot.Core.Constants` | `CallbackPrefixes.cs` | `static class CallbackPrefixes` — **полный список в §6** |
| `TelegramBot.Core.Constants` | `CommandCodes.cs` | `static class CommandCodes` (`Pdf="PDF"`, `Dwg="DWG"`, `Nwc="NWC"`, `Ifc="IFC"`, `BimDoc="BIMDOC"`, `ClashRep="CLASHREP"`, `AutoRes="AUTORES"`) |
| `TelegramBot.Core.Constants` | `CommandPriorities.cs` | `static class CommandPriorities` (`Critical=1`, `High=2`, `Medium=3`, `Low=4`, `Lowest=5`, `Default=50`) |
| `TelegramBot.Core.Constants` | `CommandStatuses.cs` | `[Obsolete] static class CommandStatuses` — алиас на `Statuses` |
| `TelegramBot.Core.Constants` | `Statuses.cs` | `static class Statuses` (`Pending="pending"`, `Processing="processing"`, `Done="Done"`, `Failed="Failed"`, `Deleted="Deleted"`, `FinalStatuses`, `ActiveStatuses`) |
| `TelegramBot.Core.DTOs` | `MessageDto.cs` | `class MessageDto` |
| `TelegramBot.Core.DTOs` | `CallbackQueryDto.cs` | `class CallbackQueryDto` |
| `TelegramBot.Core.Interfaces` | `ICallbackDispatcher.cs` | `interface ICallbackDispatcher` |
| `TelegramBot.Core.Interfaces` | `ICallbackHandler.cs` | `interface ICallbackHandler` |
| `TelegramBot.Core.Interfaces` | `ICommandAppService.cs` | `interface ICommandAppService` |
| `TelegramBot.Core.Interfaces` | `ICommandDataService.cs` | `interface ICommandDataService` |
| `TelegramBot.Core.Interfaces` | `IDatabaseInitializer.cs` | `interface IDatabaseInitializer` |
| `TelegramBot.Core.Interfaces` | `IMessageTrackingDataService.cs` | `interface IMessageTrackingDataService` |
| `TelegramBot.Core.Interfaces` | `INotificationDataService.cs` | `interface INotificationDataService` |
| `TelegramBot.Core.Interfaces` | `ISessionDataService.cs` | `interface ISessionDataService` |
| `TelegramBot.Core.Interfaces` | `ISessionManager.cs` | `interface ISessionManager` |
| `TelegramBot.Core.Interfaces` | `IUserDataService.cs` | `interface IUserDataService` |
| `TelegramBot.Core.Models` | `BotUser.cs` | `class BotUser` (`UserId`, `Username?`, `Role`, `Status`, `CreatedAt`, `UpdatedAt`) |
| `TelegramBot.Core.Models` | `CallbackContext.cs` | `sealed class CallbackContext` |
| `TelegramBot.Core.Models` | `CallbackPrefixes.cs` | **(файл пустой — только EOF)** |
| `TelegramBot.Core.Models` | `ParsedCallback.cs` | `readonly record struct ParsedCallback(Prefix, Argument)` + `static class CallbackDataParser` |
| `TelegramBot.Core.Models` | `PendingCommand.cs` | `class PendingCommand` |
| `TelegramBot.Core.Models` | `SessionCommands.cs` | `class SessionCommands` |
| `TelegramBot.Core.Models` | `SessionStatus.cs` | `class SessionStatus` (`Status`, `ProjectName?`, `CreatedAt`, `TotalFiles`, `DoneFiles`, `FailedFiles`, `ProcessingFiles`, `PendingFiles`) |
| `TelegramBot.Core.Models` | `SessionsList.cs` | `class SessionsList` |
| `TelegramBot.Core.Models` | `UserAccessStatus.cs` | `enum UserAccessStatus { Pending=0, Approved=1, Rejected=2, Blocked=3 }` |
| `TelegramBot.Core.Models` | `UserRole.cs` | `enum UserRole { User=0, Admin=1 }` |
| `TelegramBot.Core.Models` | `UserSession.cs` | `class UserSession` (с `_commandLock`, `_selectionLock`) |
| `TelegramBot.Core.Services` | `RateLimiter.cs` | `class RateLimiter` |

### 4.2 `TelegramBot.Data`

| Namespace | Файл | Типы |
|-----------|------|------|
| `TelegramBot.Data` | `DatabaseInitializer.cs` | реализация `IDatabaseInitializer` (на `PostgresDataService`) |
| `TelegramBot.Data` | `NpgsqlHelper.cs` | `static class NpgsqlHelper` (`CreateOpenConnectionAsync`) |
| `TelegramBot.Data` | `PostgresDataService.cs` | `sealed class PostgresDataService` — реализует `IUserDataService`, `ISessionDataService`, `ICommandDataService`, `IMessageTrackingDataService`, `INotificationDataService`, `IDatabaseInitializer` |
| `TelegramBot.Data` | `Sql/Queries.Schema.cs` | `internal static partial class SqlQueries { internal static class Schema { ... } }` |
| `TelegramBot.Data` | `Sql/Queries.Commands.cs` | `SqlQueries.Commands.*` (см. §5) |
| `TelegramBot.Data` | `Sql/Queries.Sessions.cs` | `SqlQueries.Sessions.*` |
| `TelegramBot.Data` | `Sql/Queries.TrackedMessages.cs` | `SqlQueries.TrackedMessages.*` |
| `TelegramBot.Data` | `Sql/Queries.Users.cs` | `SqlQueries.Users.*` |

### 4.3 `TelegramBot.Server.*`

| Namespace | Файл | Тип |
|-----------|------|-----|
| `TelegramBot.Server` | `Program.cs` | `static class Program` (Main) |
| `TelegramBot.Server.Config` | `BotCommandsSetup.cs` | `static class BotCommandsSetup` |
| `TelegramBot.Server.Constants` | `HandlerPriorities.cs` | `internal static class HandlerPriorities` (`AccessRequest=0`, `FileNavigation=10`, `FileSelection=20`, `Default=100`) |
| `TelegramBot.Server.Extensions` | `DependencyInjectionExtensions.cs` | `static class DependencyInjectionExtensions` (`AddTelegramBotServer`, `AddConfiguration`, `AddCallbackHandlers`, `AddApplicationServices`, `AddInfrastructureServices`, `AddTelegramServices`) |
| `TelegramBot.Server.Helpers` | `MarkdownHelper.cs` | `static class MarkdownHelper` |
| `TelegramBot.Server.Helpers` | `SerilogSetup.cs` | `static class SerilogSetup` |
| `TelegramBot.Server.Interfaces` | `IKeyboardBuilder.cs` | `interface IKeyboardBuilder` |
| `TelegramBot.Server.Interfaces` | `ISlashCommandService.cs` | `interface ISlashCommandService` |
| `TelegramBot.Server.Interfaces` | `ITelegramOutputService.cs` | `interface ITelegramOutputService` |
| `TelegramBot.Server.Models` | `CommandDefinition.cs` | содержит `enum CommandGroup` |
| `TelegramBot.Server.Services.Application` | `CallbackDispatcher.cs` | `sealed class CallbackDispatcher : ICallbackDispatcher` |
| `TelegramBot.Server.Services.Application` | `CommandAppService.cs` | `sealed class CommandAppService : ICommandAppService` |
| `TelegramBot.Server.Services.Application` | `SessionManager.cs` | `class SessionManager : ISessionManager, IDisposable` |
| `TelegramBot.Server.Services.Application` | `SlashCommandService.cs` | `sealed class SlashCommandService : ISlashCommandService` |
| `TelegramBot.Server.Services.Application.Handlers` | `AccessRequestHandler.cs` | `sealed class AccessRequestHandler : CallbackHandlerBase` |
| `TelegramBot.Server.Services.Application.Handlers` | `CallbackHandlerBase.cs` | `abstract class CallbackHandlerBase : ICallbackHandler` |
| `TelegramBot.Server.Services.Application.Handlers` | `CommandSelectionHandler.cs` | `sealed class CommandSelectionHandler : CallbackHandlerBase` |
| `TelegramBot.Server.Services.Application.Handlers` | `CommandToggleHandler.cs` | `sealed class CommandToggleHandler : CallbackHandlerBase` |
| `TelegramBot.Server.Services.Application.Handlers` | `FileNavigationHandler.cs` | `sealed class FileNavigationHandler : CallbackHandlerBase` |
| `TelegramBot.Server.Services.Application.Handlers` | `FileSelectionHandler.cs` | `sealed class FileSelectionHandler : CallbackHandlerBase` |
| `TelegramBot.Server.Services.Application.Handlers` | `HandlerHelpers.cs` | `internal static class HandlerHelpers` (`SendActionsReplyKeyboardAsync` × 2 overloads) |
| `TelegramBot.Server.Services.Application.Handlers` | `SessionManagementHandler.cs` | `sealed class SessionManagementHandler : CallbackHandlerBase` |
| `TelegramBot.Server.Services.Infrastructure.FileSystem` | `FileSystemBrowser.cs` | `class FileSystemBrowser` |
| `TelegramBot.Server.Services.Infrastructure.Telegram` | `CommandNotificationService.cs` | `sealed class CommandNotificationService` (BackgroundService) |
| `TelegramBot.Server.Services.Infrastructure.Telegram` | `KeyboardBuilder.cs` | `class KeyboardBuilder : IKeyboardBuilder` |
| `TelegramBot.Server.Services.Infrastructure.Telegram` | `TelegramBotHostedService.cs` | `class TelegramBotHostedService` (BackgroundService) |
| `TelegramBot.Server.Services.Infrastructure.Telegram` | `TelegramOutputService.cs` | `class TelegramOutputService : ITelegramOutputService` |
| `TelegramBot.Server.Services.Infrastructure.Telegram` | `TelegramUpdateMapper.cs` | `class TelegramUpdateMapper` |

### 4.4 `TelegramBot.Worker.*`

| Namespace | Файл | Тип |
|-----------|------|-----|
| `TelegramBot.Worker` | `Program.cs` | `static class Program` (Main) |
| `TelegramBot.Worker.BimLib.Config` | `BimLib/Config/BimIntegrationOptions.cs` | `sealed class BimIntegrationOptions` |
| `TelegramBot.Worker.BimLib.Interfaces` | `BimLib/Interfaces/IRevitVersionDetector.cs` | `interface IRevitVersionDetector` |
| `TelegramBot.Worker.BimLib.Interfaces` | `BimLib/Interfaces/INavisworksPathResolver.cs` | `interface INavisworksPathResolver` |
| `TelegramBot.Worker.BimLib.Models` | `BimLib/Models/RevitDetectedVersion.cs` | `sealed record RevitDetectedVersion` |
| `TelegramBot.Worker.BimLib.Models` | `BimLib/Models/RevitProcessHealth.cs` | `sealed record RevitProcessHealth(...)` + `enum RevitProcessStatus { Healthy, NotResponding, Error }` |
| `TelegramBot.Worker.BimLib.Monitor` | `BimLib/Monitor/DialogDismisser.cs` | `sealed class DialogDismisser` |
| `TelegramBot.Worker.BimLib.Monitor` | `BimLib/Monitor/NavisworksProcessTracker.cs` | `internal sealed class NavisworksProcessTracker` |
| `TelegramBot.Worker.BimLib.Monitor` | `BimLib/Monitor/ProcessHealthHelper.cs` | `internal static class ProcessHealthHelper` |
| `TelegramBot.Worker.BimLib.Monitor` | `BimLib/Monitor/RevitProcessTracker.cs` | `internal sealed class RevitProcessTracker` |
| `TelegramBot.Worker.BimLib.Monitor` | `BimLib/Monitor/WindowInfo.cs` | `internal sealed class WindowInfo` |
| `TelegramBot.Worker.BimLib.Monitor` | `BimLib/Monitor/WindowUtil.cs` | `internal static class WindowUtil` |
| `TelegramBot.Worker.BimLib.Native` | `BimLib/Native/User32.cs` | `internal static class User32` (P/Invoke) |
| `TelegramBot.Worker.BimLib.Native` | `BimLib/Native/Win32Types.cs` | `internal static class Win32Consts` (константы: `BmClick`, `WmLButtonDown` и т.д.) |
| `TelegramBot.Worker.BimLib.Services` | `BimLib/Services/NavisworksPathResolver.cs` | `sealed class NavisworksPathResolver : INavisworksPathResolver` |
| `TelegramBot.Worker.BimLib.Services` | `BimLib/Services/RevitPathResolver.cs` | `sealed class RevitPathResolver` (НЕ наследует интерфейс) |
| `TelegramBot.Worker.BimLib.Services` | `BimLib/Services/RevitVersionDetector.cs` | `sealed class RevitVersionDetector : IRevitVersionDetector` |
| `TelegramBot.Worker.Helpers` | `Helpers/SerilogSetup.cs` | `static class SerilogSetup` |
| `TelegramBot.Worker.Services` | `Services/BimLibLogFilter.cs` | `static class BimLibLogFilter` (`IsBimLibEvent` predicate) |
| `TelegramBot.Worker.Services` | `Services/CommandExecutionService.cs` | `sealed class CommandExecutionService` (BackgroundService) |

### 4.5 Зарегистрированные в DI сервисы (фактически)

**`TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`:**

- `AddConfiguration`: `FileSystemOptions`, `BotOptions`, `RateLimitOptions`.
- `AddCallbackHandlers`:
  - `ICallbackHandler → AccessRequestHandler`
  - `ICallbackHandler → FileNavigationHandler`
  - `ICallbackHandler → FileSelectionHandler`
  - `ICallbackHandler → CommandToggleHandler`
  - `ICallbackHandler → SessionManagementHandler`
  - `ICallbackHandler → CommandSelectionHandler`
  - `ICallbackDispatcher → CallbackDispatcher`
- `AddApplicationServices`:
  - `ICommandAppService → CommandAppService`
  - `RateLimiter`
  - `ISlashCommandService → SlashCommandService`
  - `ISessionManager → SessionManager` (с `TimeSpan.FromMinutes(5)`)
- `AddInfrastructureServices`:
  - `IUserDataService → PostgresDataService`
  - `ISessionDataService → PostgresDataService`
  - `ICommandDataService → PostgresDataService`
  - `IMessageTrackingDataService → PostgresDataService`
  - `INotificationDataService → PostgresDataService`
  - `IDatabaseInitializer → PostgresDataService`
  - `FileSystemBrowser` (concrete)
- `AddTelegramServices`:
  - `ITelegramBotClient → new TelegramBotClient(Token)` (через factory)
  - `ITelegramOutputService → TelegramOutputService` (с `botOptions.AdminUserIds?.FirstOrDefault()` как `adminId`)
  - `TelegramUpdateMapper` (concrete)
  - `IKeyboardBuilder → KeyboardBuilder`
  - `IHostedService → TelegramBotHostedService`
  - `IHostedService → CommandNotificationService`

**`TelegramBot.Worker/Program.cs`:**

- `IUserDataService, ISessionDataService, ICommandDataService, IMessageTrackingDataService, INotificationDataService, IDatabaseInitializer` — все → `PostgresDataService`.
- `WorkerOptions` (bind → `Worker` section).
- `BimIntegrationOptions` (bind → `BimIntegration` section).
- BIM-сервисы:
  - `IRevitVersionDetector → RevitVersionDetector`
  - `RevitPathResolver` (concrete, без интерфейса)
  - `RevitProcessTracker` (concrete, без интерфейса)
  - `DialogDismisser` (concrete, без интерфейса)
  - `INavisworksPathResolver → NavisworksPathResolver`
  - `NavisworksProcessTracker` (concrete, без интерфейса)
- `IHostedService → CommandExecutionService`

> **Расхождение с AGENTS.md:** В AGENTS.md сказано
> *«DI registration: BimLib services are registered directly in `Worker/Program.cs` (no separate `AddBimIntegration()` extension method)»* — это **совпадает с реальностью**.
> Но AGENTS.md также пишет: *«Removed interfaces (concrete classes only): `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`»*.
> В реальности: интерфейс `IRevitPathResolver` (на самом деле `IRevitVersionDetector` — единственный интерфейс
> в `BimLib/Interfaces/`, кроме `INavisworksPathResolver`); `RevitPathResolver`, `RevitProcessTracker`,
> `NavisworksProcessTracker` — действительно concrete-классы. Так что в AGENTS.md
> имя «`IRevitPathResolver`» перепутано с `IRevitVersionDetector`.

---

## 5. SQL-схема

### 5.1 Таблицы (фактически создаются в `Queries.Schema.cs`)

| Таблица | Колонки | NOT NULL / DEFAULT |
|---------|---------|---------------------|
| **BotUsers** | `UserId BIGINT PK`, `Username TEXT`, `Role INTEGER NOT NULL DEFAULT 0`, `Status INTEGER NOT NULL DEFAULT 0`, `CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()`, `UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()` | — |
| **Sessions** | `SessionId SERIAL PK`, `UserId BIGINT NOT NULL`, `Username TEXT`, `PriorityId INTEGER NOT NULL DEFAULT 0`, `Status TEXT NOT NULL DEFAULT 'pending'`, **`ProjectName TEXT`** (nullable, добавлен через миграцию), `CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()`, `FilesAmount INTEGER`, `UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()` | `PriorityId`, `Status`, `ProjectName`, `FilesAmount`, `UpdatedAt` добавляются миграцией `EnsureSessionsColumns` |
| **Commands** | `CommandId SERIAL PK`, `SessionId INTEGER NOT NULL REFERENCES Sessions(SessionId)`, `CommandText TEXT NOT NULL`, `FilePath TEXT`, `ExecutionOrder INTEGER NOT NULL`, `Status TEXT NOT NULL DEFAULT 'pending'`, `CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()`, `StartedAt TIMESTAMPTZ`, `CompletedAt TIMESTAMPTZ`, `GUID TEXT`, `Lease INTEGER`, **`Partition TEXT`** (nullable), **`Priority INTEGER NOT NULL DEFAULT 50`**, `ProcessId INTEGER`, `ErrorMessage TEXT`, `RetryCount INTEGER NOT NULL DEFAULT 0`, `NextRetryAt TIMESTAMPTZ`, **`Progress INTEGER NOT NULL DEFAULT 0`**, **`Result TEXT`**, `UpdatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()` | `Progress`, `Result`, `UpdatedAt` добавляются миграцией `EnsureCommandsColumns` |
| **TrackedMessages** | `MessageId SERIAL PK`, `SessionId INTEGER REFERENCES Sessions(SessionId)` (nullable после `MakeTrackedMessagesSessionNullable`), `ChatId BIGINT NOT NULL`, `MessageIdPg INTEGER NOT NULL`, `CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()` | — |

### 5.2 Индексы (`CreateIndexes`)

- `idx_commands_status (Status)`
- `idx_commands_session (SessionId)`
- `idx_commands_status_lease (Status, Lease) WHERE Status='processing'`
- `idx_sessions_user_created (UserId, CreatedAt DESC)`
- `idx_commands_pending_priority (Status, Priority ASC, CreatedAt ASC, CommandId ASC) WHERE Status='pending'`
- `idx_commands_partition_status (Partition, Status)`
- **`UNIQUE idx_commands_unique (SessionId, CommandText, FilePath)`** — гарантирует отсутствие дублей
- `idx_tracked_messages_session (SessionId)`
- `idx_tracked_messages_chat (ChatId)`
- `idx_commands_updated_at (UpdatedAt DESC)`
- Также в `CreateIndexes` есть `DROP INDEX IF EXISTS idx_commands_pending_priority;` (пересоздаётся)

### 5.3 NOTIFY-каналы

| Канал | Кто слушает | Кто публикует | Payload |
|-------|-------------|---------------|---------|
| `new_tasks` | `TelegramBot.Worker.Services.CommandExecutionService` (BackgroundService; `NpgsqlCommand("LISTEN new_tasks;")` в строке 123) | `PostgresDataService.CreateSessionWithCommandsAsync` (строка 127): `SELECT pg_notify('new_tasks', '')` | пустая строка (триггер «проверь очередь») |
| `command_completed` | `TelegramBot.Server.Services.Infrastructure.Telegram.CommandNotificationService` (BackgroundService; `LISTEN command_completed;` в строке 48) | `PostgresDataService.NotifyCommandCompletedAsync` (строка 449-461): `SELECT pg_notify('command_completed', @Payload)`, payload = `$"{userId}\|{sessionId}\|{doneCount}\|{totalCount}\|{projectName ?? ""}"` | pipe-delimited строка |

> **Расхождение с AGENTS.md:** В AGENTS.md сказано *«`command_completed` payload: `UserId|SessionId|Done|Total|ProjectName`»* — это совпадает с реальным кодом (см. `PostgresDataService.cs:453`).

---

## 6. Callback-префиксы и хендлеры

### 6.1 Все префиксы в `TelegramBot.Core/Constants/CallbackPrefixes.cs`

```csharp
public static class CallbackPrefixes
{
    public const string GoToParent            = "GOTOPARENT:";
    public const string File                  = "FILE:";
    public const string Pdf                   = "PDF:";
    public const string Dwg                   = "DWG:";
    public const string Nwc                   = "NWC:";
    public const string Ifc                   = "IFC:";
    public const string BimDoc                = "BIMDOC:";
    public const string ClashRep              = "CLASHREP:";
    public const string AutoRes               = "AUTORES:";
    public const string ApplyCommands         = "APPLYCOMMANDS:";
    public const string CancelCommandSelection= "CANCELCOMMANDSSEL:";
    public const string SessionDetails        = "SESSIONDETAILS:";
    public const string DeleteSession         = "DELETESESSION:";
    public const string DeleteCommand         = "DELETECOMMAND:";
    public const string ConfirmDeleteSession  = "CONFIRMDELETESESSION:";
    public const string ConfirmDeleteCommand  = "CONFIRMDELETECOMMAND:";
    public const string DeleteSessionByType       = "DELETESESSIONBYTYPE:";
    public const string ConfirmDeleteSessionByType= "CONFIRMDELETESESSIONBYTYPE:";
    public const string RequestAccess         = "REQACCESS:";
    public const string ApproveUser           = "APPROVEUSER:";
    public const string RejectUser            = "REJECTUSER:";
}
```

Все 22 префикса имеют суффикс `:` (формат: `"PREFIX:argument"`).

> **Дубль:** `TelegramBot.Core/Models/CallbackPrefixes.cs` — пустой файл
> (содержит только `namespace TelegramBot.Core.Models;` и EOF).
> Реальные префиксы — в `TelegramBot.Core/Constants/CallbackPrefixes.cs`.

### 6.2 Маппинг хендлеров на префиксы (фактически)

| Хендлер | Файл | Priority | Поддерживаемые префиксы | Реализованные действия |
|---------|------|----------|-------------------------|----------------------|
| `AccessRequestHandler` | `Server/Services/Application/Handlers/AccessRequestHandler.cs` | **0** (`HandlerPriorities.AccessRequest`) | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` | `HandleRequestAccessAsync`, `HandleApproveAsync`, `HandleRejectAsync` |
| `FileNavigationHandler` | `Server/Services/Application/Handlers/FileNavigationHandler.cs` | **10** (`HandlerPriorities.FileNavigation`) | `GOTOPARENT:` | навигация вверх по директориям, проверка `IsPathWithinRoot` |
| `FileSelectionHandler` | `Server/Services/Application/Handlers/FileSelectionHandler.cs` | **20** (`HandlerPriorities.FileSelection`) | `FILE:` | toggle файла в сессии; на project-level одиночный выбор, иначе множественный |
| `CommandToggleHandler` | `Server/Services/Application/Handlers/CommandToggleHandler.cs` | **100** (default) | `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:`, `CLASHREP:`, `AUTORES:` (через `CommandCatalog.TryGetByPrefix`) | add/remove pending command и редактирование клавиатуры |
| `CommandSelectionHandler` | `Server/Services/Application/Handlers/CommandSelectionHandler.cs` | **100** (default) | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` | подтверждение выбора команд / отмена с очисткой |
| `SessionManagementHandler` | `Server/Services/Application/Handlers/SessionManagementHandler.cs` | **100** (default) | `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `CONFIRMDELETESESSION:`, `CONFIRMDELETECOMMAND:`, `DELETESESSIONBYTYPE:`, `CONFIRMDELETESESSIONBYTYPE:` | просмотр сессий, soft-delete сессии/команды/по типу |

### 6.3 Сопоставление «описано, но не реализовано» / «реализовано, но не описано»

**Все 22 префикса из `CallbackPrefixes` имеют обработчика** (если учитывать
`CommandToggleHandler`, который динамически подхватывает все семь префиксов
команд через `CommandCatalog.TryGetByPrefix`).

> **Замечание о `CommandToggleHandler`:** в файле не задан явный
> `SupportedPrefixes`; `CanHandle()` переопределён и использует
> `CommandCatalog.TryGetByPrefix(prefix, out _)`. Это означает, что префиксы
> `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:`, `CLASHREP:`, `AUTORES:`
> обрабатываются через `CommandCatalog` (определён в
> `TelegramBot.Server/Config/BotCommandsSetup.cs`), а не через
> статический набор в хендлере.

**Префиксы, описанные, но не имеющие выделенного хендлера:** отсутствуют.

**Префиксы, реализованные, но не описанные в `CallbackPrefixes`:** отсутствуют.

> **Расхождение с AGENTS.md:** В AGENTS.md указана иерархия приоритетов
> `AccessRequest (0) > FileNavigation (10) > FileSelection (20) > CommandToggle, SessionManagement, CommandSelection (100)`.
> Реально: `HandlerPriorities.cs` объявляет **только** `AccessRequest=0`, `FileNavigation=10`, `FileSelection=20`, `Default=100`.
> Классы `CommandToggleHandler`, `SessionManagementHandler`, `CommandSelectionHandler`
> **не переопределяют** `Priority` и наследуют `HandlerPriorities.Default = 100` —
> это **совпадает** с порядком, описанным в AGENTS.md.

---

## 7. Config-ключи (что реально читается из appsettings)

### 7.1 Server (binding через `DependencyInjectionExtensions.AddConfiguration`)

| Section | Класс | Поля | Default |
|---------|-------|------|---------|
| `TelegramBot` (`BotOptions.SectionName`) | `BotOptions` | `Token: string` (required), `AdminUserIds: long[]` (default `[]`) | — |
| `FileSystem` (`FileSystemOptions.SectionName`) | `FileSystemOptions` | `RootPath: string` (**required**, validated `!IsNullOrWhiteSpace` + `Directory.Exists`), `RvtDirectoryName="01_RVT"`, `ProjectDirectoryName="01_PROJECT"`, `RevitFileExtension=".rvt"`, `SectionFolderPattern=@"^(\d{2}|\d{3}|I{1,3})_"` | см. §7.1 |
| `RateLimit` (`RateLimitOptions.SectionName`) | `RateLimitOptions` | `MaxRequests=10`, `WindowSeconds=60`, `MaxFilesPerUserPerDay=1000` | см. §7.1 |

**Валидации:**
- `FileSystemOptions.RootPath` — `Validate(!IsNullOrWhiteSpace(...))` + `Validate(Directory.Exists(...))`.
- `BotOptions.Token` — `Validate(!IsNullOrWhiteSpace(...))`.

**Из `appsettings.json` НЕ читаются** (отсутствуют в binding), но упоминаются в AGENTS.md:

- ❌ `Worker:*` (WorkerOptions) — никогда не биндится в Server, это секция Worker.
- ❌ `BimIntegration:*` — никогда не биндится в Server.

### 7.2 Worker (binding в `Program.cs.ConfigureServices`)

| Section | Класс | Поля | Default |
|---------|-------|------|---------|
| `Worker` (`WorkerOptions.SectionName`) | `WorkerOptions` | `ProcessTimeoutSeconds=10800`, `MaxRetries=5`, `RetryDelayBaseSeconds=60`, `CompletedSessionRetentionDays=30`, `Partitions`, `Commands` (с `PDF/DWG/IFC/BIMDOC/NWC/CLASHREP/AUTORES`) | см. §7.2 |
| `BimIntegration` (`BimIntegrationOptions.SectionName`) | `BimIntegrationOptions` | `MinSupportedVersion=2018`, `MaxSupportedVersion=2026`, `RevitInstallRoot=@"C:\Program Files\Autodesk"` | см. §7.2 |

**Из `appsettings.json` НЕ читаются** в Worker, но упоминаются в AGENTS.md:

- ❌ `TelegramBot:Token` / `TelegramBot:AdminUserIds` — Worker не использует Telegram.Bot и не нуждается в токене.
- ❌ `FileSystem:*` — Worker не имеет UI и не работает с FileSystemBrowser.

**`ConnectionStrings:Postgres`** — Worker использует `Npgsql` напрямую, но
**не биндит** `IConfiguration` через опции. Строка подключения
передаётся в `PostgresDataService` через его конструктор
(закоммиченная `appsettings.json` обоих проектов содержит
`"ConnectionStrings:Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"`).

> **Замечание:** проверка фактической регистрации строки подключения
> в DI требует чтения `PostgresDataService.cs` (конструктор). В текущем
> `Program.cs` Worker binding `ConnectionStrings` не объявлен явно,
> но `PostgresDataService` (Singleton) создаётся — значит, конфигурация
> попадает к нему через стандартный binding, который происходит внутри
> `PostgresDataService` (нужно проверить отдельно; в AGENTS.md сказано,
> что `NpgsqlHelper.CreateOpenConnectionAsync` принимает connection string,
> передаваемую из `PostgresDataService`).

---

## 8. Appsettings-файлы

### 8.1 `TelegramBot.Server/appsettings.json` (закоммичен)

Корневые секции:

- `Serilog` — `Using: [Serilog.Sinks.Console, Serilog.Sinks.Seq]`, `MinimumLevel.Default=Information`, `Override: { Microsoft: Warning, System: Warning }`, `Enrich: [FromLogContext]`, `WriteTo: [Console, Seq(serverUrl=http://localhost:5341)]`.
- `ConnectionStrings.Postgres = "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"`.
- `RateLimit` — `MaxRequests=30` ⚠️ (НЕ совпадает с default `RateLimitOptions.MaxRequests=10`!), `WindowSeconds=60`, `MaxFilesPerUserPerDay=1000`.
- `FileSystem` — `RvtDirectoryName="01_RVT"`, `ProjectDirectoryName="01_PROJECT"`, `RevitFileExtension=".rvt"`, `SectionFolderPattern=@"^(\d{2}|\d{3}|I{1,3})_"`. **Не содержит `RootPath`** (должен быть в Local).

> **Расхождение с AGENTS.md:** В AGENTS.md указано
> *"`TelegramBot.Server/appsettings.json` — committed, contains Serilog config, `FileSystem` options, and `ConnectionStrings:Postgres`"*.
> Реально appsettings.json Server содержит Serilog, ConnectionStrings, RateLimit и FileSystem.
> `TelegramBot:Token` и `TelegramBot:AdminUserIds` НЕ в закоммиченном файле — это **совпадает** с практикой «секреты только в Local».

### 8.2 `TelegramBot.Server/appsettings.Local.json` (закоммичен — **проблема**)

Содержимое:

```json
{
  "TelegramBot": {
    "Token": "8851757963:AAEx4aLfADT94mPq0rJr5AwZuTvej_gtaMs",
    "AdminUserIds": [385753167]
  },
  "FileSystem": { "RootPath": "B:\\" },
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=telegram_bot;Username=postgres;Password=postgres"
  }
}
```

> **⚠️ SECURITY ISSUE:** `appsettings.Local.json` **не указан** в `.gitignore`
> (.gitignore содержит только `.env`, `.env.local`).
> Файл `TelegramBot.Server/appsettings.Local.json` **закоммичен** в git
> (подтверждено `git ls-files | grep Local`) и содержит:
> - реальный Telegram-токен бота (`8851757963:AAEx4aLfADT94mPq0rJr5AwZuTvej_gtaMs`),
> - ID администратора (`385753167`),
> - production-like connection string к PostgreSQL.
> Это противоречит практике «secrets in Local», описанной в AGENTS.md.
> Документация утверждает, что Local — gitignored, но фактически — нет.

### 8.3 `TelegramBot.Worker/appsettings.json` (закоммичен)

Корневые секции:

- `Serilog` — `Using: [Serilog.Sinks.Console, Serilog.Sinks.Seq]`, `MinimumLevel.Default=Information`, `Override: { Microsoft: Warning, System: Warning }`, `WriteTo: [Console, Seq(serverUrl=http://localhost:5341)]`.
- `BimIntegration` — `MinSupportedVersion=2018`, `MaxSupportedVersion=2026`, `RevitInstallRoot="C:\\Program Files\\Autodesk"`.
- `ConnectionStrings.Postgres = "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"`.
- `Worker`:
  - `ProcessTimeoutSeconds=10800`.
  - `CompletedSessionRetentionDays=30`.
  - `Partitions`: `1:3, 2:5, 3:3, 4:1, 5:1`.
  - `Commands`:
    - `PDF`, `DWG`, `IFC`, `BIMDOC` → `Revit.exe`, `AllowedExtensions: [.rvt, .rfa]`, `ArgumentsTemplate: /command "{CommandText}" "{FilePath}"`.
    - `NWC`, `CLASHREP` → `FileConvert.exe`, `AllowedExtensions: [.nwc, .nwd, .nwf]`.
    - `AUTORES` → `python`, `AllowedExtensions: [.rvt, .ifc, .nwc]`, `ArgumentsTemplate: ai_agent.py --command "{CommandText}" --file "{FilePath}"`.

> **Расхождение с AGENTS.md:** В AGENTS.md указано
> *"`Worker:CompletedSessionRetentionDays` — auto-cleanup retention for inactive sessions; `0` disables it"*.
> Реально — да, `WorkerOptions.CompletedSessionRetentionDays` имеет default `30`, и в
> `appsettings.json` указано `30`. Документация совпадает.

### 8.4 `TelegramBot.Worker/appsettings.Local.json` (закоммичен — **та же проблема**)

Содержимое:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=telegram_bot;Username=postgres;Password=postgres"
  },
  "Worker": {
    "Commands": {
      "PDF": { "ExecutablePath": "Revit.exe" },
      "DWG": { "ExecutablePath": "Revit.exe" },
      "IFC": { "ExecutablePath": "Revit.exe" },
      "BIMDOC": { "ExecutablePath": "Revit.exe" },
      "NWC": { "ExecutablePath": "FileConvert.exe" },
      "CLASHREP": { "ExecutablePath": "FileConvert.exe" },
      "AUTORES": { "ExecutablePath": "python" }
    }
  }
}
```

> **⚠️ SECURITY ISSUE:** `appsettings.Local.json` Worker тоже **закоммичен**
> в git (хотя не содержит токенов). Это та же проблема: `.gitignore` не
> исключает файлы Local.

---

## 9. Build & Run команды (проверка)

### 9.1 `TelegramBot.slnx` существует

✅ Файл присутствует в корне репо (470 байт). Содержимое:

```xml
<Solution>
  <Folder Name="/Docs/">
    <File Path="Docs/CommandExecutionAlgorithm.md" />
    <File Path="Docs/execution-algorithm.md" />
    <File Path="Docs/qodana-setup.md" />
  </Folder>
  <Project Path="TelegramBot.Core\TelegramBot.Core.csproj" />
  <Project Path="TelegramBot.Data\TelegramBot.Data.csproj" />
  <Project Path="TelegramBot.Server\TelegramBot.Server.csproj" />
  <Project Path="TelegramBot.Worker\TelegramBot.Worker.csproj" />
</Solution>
```

### 9.2 Имена `.csproj` совпадают с командами AGENTS.md

| Команда из AGENTS.md | Реальный файл | Совпадает? |
|----------------------|---------------|------------|
| `dotnet build TelegramBot.slnx` | `TelegramBot.slnx` | ✅ |
| `dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj` | `TelegramBot.Server/TelegramBot.Server.csproj` | ✅ |
| `dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj` | `TelegramBot.Worker/TelegramBot.Worker.csproj` | ✅ |
| `dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release` | `TelegramBot.Server/TelegramBot.Server.csproj` | ✅ |

---

## 10. Документы на диске

### 10.1 `.md` файлы в корне репо

- `AGENTS.md` (25 972 байт) — инструкции для агентов (см. проект).
- `README.md` (7 056 байт).
- `ROADMAP.md` (24 624 байт).
- `LICENSE.txt` — не `.md`, но текстовый (1 090 байт).

### 10.2 `.md` файлы в `Docs/`

- `Docs/CommandExecutionAlgorithm.md` (407 строк, **ASCII-art sequence diagram**).
- `Docs/execution-algorithm.md` (1 431 строка, **полная спецификация алгоритма**).
- `Docs/qodana-setup.md` (68 строк).

> **⚠️ ДУБЛЬ / DUP-DOC (требует решения):**
> `Docs/CommandExecutionAlgorithm.md` начинается с цитаты:
> *«Текстовое представление диаграммы `CommandExecutionAlgorithm.puml`
> Полная спецификация: [execution-algorithm.md](execution-algorithm.md)»*.
> То есть файл `CommandExecutionAlgorithm.md` явно является **summary**,
> а `execution-algorithm.md` — **полной** версией. Этот дубль **зафиксирован
> в самом тексте** (cross-link), но **оба файла реально существуют на диске**,
> оба перечислены в `TelegramBot.slnx` (`<Folder Name="/Docs/">`), и оба
> присутствуют в `git ls-files`.

### 10.3 `.md` файлы в `audit/`

- `audit/doc-claims.md` (создан предыдущей задачей, 110 416 байт).
- `audit/code-facts.md` — **этот файл**.

### 10.4 `.md` файлы вне корня и `Docs/` (внутренние)

- `.claude/skills/gitnexus/*/SKILL.md` — служебные навыки для агентов.
- `.github/copilot-instructions.md` — инструкции для Copilot.
- `.omo/notepads/session-status-ui-improvement/{decisions,issues,learnings,problems}.md` — заметки сессии.
- `.omo/plans/session-status-ui-improvement.md` — план.

> Вне `.claude/`, `.omo/`, `.github/`, `.opencode/`, `.gitnexus/` и т.п.
> (т.е. в «пользовательских» директориях) `.md` файлов больше нет.

---

## Дополнительные факты, обнаруженные в ходе аудита

### 11.1 In-memory `ConcurrentDictionary` для счётчика сессий

`TelegramBot.Worker.Services.CommandExecutionService`:

- `private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();` (строка 50)
- `private readonly ConcurrentDictionary<int, int> _sessionRemaining = new();` (строка 54)
- `AddOrUpdate(group.Key, group.Count(), (_, existing) => existing + group.Count())` при `ClaimPendingCommandsAsync` (строка 414)
- `_sessionRemaining.TryRemove(cmd.SessionId, out _)` (строка 795) при завершении

Совпадает с описанием в AGENTS.md.

### 11.2 Платформенные ограничения

- `TelegramBot.Server/Program.cs:14` — `[SupportedOSPlatform("windows")]` на классе `Program`.
- `TelegramBot.Server/Program.cs:20-24` — runtime-проверка `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`.
- `TelegramBot.Worker/Program.cs:13` — `[assembly: SupportedOSPlatform("windows")]`.
- BimLib-сервисы: `[SupportedOSPlatform("windows")]` на классах `RevitVersionDetector`, `RevitPathResolver`, `NavisworksPathResolver`.

### 11.3 `.gitignore` — отсутствуют правила для Local-файлов

Текущее содержимое `.gitignore`:

```
packages/
bin/
obj/
*.log
.env
.env.local
.vscode/
.vs/
.idea/
.opencode/
```

> **Проблема:** `appsettings.Local.json` **не** в этом списке, поэтому оба
> Local-файла коммитятся. См. §8.2 и §8.4.

### 11.4 `Npgsql.DependencyInjection` в `.csproj`

`TelegramBot.Data.csproj` содержит `Npgsql.DependencyInjection 10.0.3`,
но **в коде нет вызовов** вроде `services.AddNpgsqlDataSource(...)` — это
просто запас, либо используется где-то ещё (нужно уточнить — но в
прочитанных файлах DI Worker/Server не использует его напрямую).

### 11.5 Прочие мелочи

- В `TelegramBot.Core/Models/CallbackPrefixes.cs` — пустой файл (только namespace и EOF). Реальные префиксы — в `Constants/CallbackPrefixes.cs`. AGENTS.md упоминает только `Constants/CallbackPrefixes.cs` — это **совпадает** с реальностью, но файл-дубль существует.
- `TelegramBot.Server/Constants/HandlerPriorities.cs` — `internal static class`, не `public`.
- `TelegramBot.Server/Services/Application/Handlers/CallbackHandlerBase.cs:11` — `SupportedPrefixes` имеет default `new HashSet<string>([])` (collection expression).
- `TelegramBot.Server/Services/Application/Handlers/HandlerHelpers.cs:13` — `internal static class`, не `public`.
- `TelegramBot.Server/Services/Infrastructure/Telegram/CommandNotificationService.cs:48` — слушает `LISTEN command_completed`.
- `TelegramBot.Worker/Services/CommandExecutionService.cs:34` — `private const string ListenChannel = "new_tasks";`.

---

## Сводка ключевых расхождений AGENTS.md ↔ реальный код

| # | Что в AGENTS.md | Что в реальности | Где смотреть |
|---|-----------------|------------------|--------------|
| 1 | Namespaces BimLib: `TelegramBot.BimLib.*` | `TelegramBot.Worker.BimLib.*` (из-за `<RootNamespace>`) | `Worker.csproj:7`, `BimLib/**/*.cs:1` |
| 2 | Интерфейс `IRevitPathResolver` был удалён | `IRevitPathResolver` не существует; `IRevitVersionDetector` существует; `RevitPathResolver` — concrete | `BimLib/Interfaces/IRevitVersionDetector.cs`, `BimLib/Services/RevitPathResolver.cs` |
| 3 | `appsettings.Local.json` — gitignored | `.gitignore` НЕ содержит правила для Local; файлы **закоммичены** | `.gitignore`, `git ls-files` |
| 4 | RateLimit `MaxRequests=10` (default в Options) | В `appsettings.json` указано `MaxRequests=30` | `Server/appsettings.json:28`, `Core/Config/RateLimitOptions.cs:6` |
| 5 | «Нет `TelegramBot.BimLib.csproj`» (это верно) | ✅ Совпадает — нет такого файла | `ls` корня |
| 6 | `Docs/execution-algorithm.md` (без упоминания `CommandExecutionAlgorithm.md`) | `slnx` содержит **оба** файла; `CommandExecutionAlgorithm.md` — краткая диаграмма, ссылается на `execution-algorithm.md` | `TelegramBot.slnx:3-4`, `Docs/CommandExecutionAlgorithm.md:1-4` |
| 7 | В `Worker/Program.cs` BimLib регистрируется вручную | ✅ Совпадает (нет `AddBimIntegration()`) | `Worker/Program.cs:44-49` |
| 8 | Колонки `Commands.Partition`, `Commands.Priority` | ✅ Совпадают (см. §5.1) | `Queries.Schema.cs:51-52` |
| 9 | Колонка `Sessions.ProjectName` | ✅ Совпадает, добавляется через `EnsureSessionsColumns` | `Queries.Schema.cs:24, 34` |
| 10 | `NOTIFY command_completed` payload | ✅ Совпадает | `PostgresDataService.cs:453` |
| 11 | `LISTEN new_tasks` | ✅ Совпадает | `CommandExecutionService.cs:34, 123` |

---

## Итог

Файл создан, фактическое состояние кодовой базы зафиксировано.
**10 разделов**, **50+ конкретных фактов** (имена файлов, namespaces, классы, версии пакетов, конфиг-ключи, префиксы, приоритеты, NOTIFY-каналы, таблицы и колонки БД, security-проблема с закоммиченными Local-файлами).
