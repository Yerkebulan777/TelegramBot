# Telegram Bot Server

Telegram-бот для навигации по файловой системе и управления сессиями экспорта/автоматизации с системой запроса доступа и ролями (User/Admin). Задачи выполняются асинхронно через отдельный Worker-процесс с PostgreSQL-очередью и событийной обработкой через LISTEN/NOTIFY.

## Документация

| Документ | Описание |
|----------|----------|
| [ROADMAP.md](ROADMAP.md) | Дорожная карта проекта (v1.0–v2.0+) |
| [Docs/execution-algorithm.md](Docs/execution-algorithm.md) | Полная спецификация алгоритма выполнения команд |
| [Docs/qodana-setup.md](Docs/qodana-setup.md) | Настройка статического анализа Qodana |
| [AGENTS.md](AGENTS.md) | Руководство для AI-агентов по работе с кодом |
| [CLAUDE.md](CLAUDE.md) | Руководство для Claude Code |
| [README.TOKEN.md](README.TOKEN.md) | Настройка токена Telegram-бота |

---

## Обзор

.NET 10 background service — Telegram-бот с long-polling (webhook-ов нет). Авторизованным пользователям доступно:

- Навигация по файловой системе через inline-клавиатуры
- Выбор RVT-файлов по секциям для экспорта
- Управление сессиями и командами (PDF, DWG, NWC, IFC и др.)
- Автоматизация: BIM-документирование, Clash Reports, AutoResolve
- Запрос доступа и подтверждение администратором
- Асинхронное выполнение задач в отдельном Worker-процессе (Revit, Navisworks, AI)
- Дневной лимит файлов на пользователя и подтверждение перед удалением сессий/команд

## Технологии

- **.NET 10** — целевая платформа (`net10.0`)
- **Telegram.Bot 22.10.0.1** — клиент Telegram Bot API
- **PostgreSQL** — хранение данных (Npgsql + Dapper 2.1.79)
- **PostgreSQL queue + events** — Worker слушает `LISTEN new_tasks` и мгновенно реагирует на новые команды; fallback-polling раз в 5 минут при потере соединения
- **Serilog** — структурированное логирование (Console + Seq)
- **OpenMcdf** — чтение OLE-потоков .rvt/.rfa-файлов (определение версии Revit)
- **Windows Registry (Microsoft.Win32)** — поиск установленных Revit/Navisworks

## Качество кода

- **dotnet format** — форматирование кода согласно `.editorconfig`.
- **Qodana** — статический анализ и поиск мертвого кода. Подробности в [Docs/qodana-setup.md](Docs/qodana-setup.md).

## Требования к платформе

⚠️ **Windows only** — проект использует Windows-specific API:
- Навигация по файловой системе (локальные пути, проверка `RuntimeInformation.IsOSPlatform` в `Program.cs`)
- **BimLib**: Windows Registry (`Microsoft.Win32`) для поиска Revit.exe/Navisworks.exe; P/Invoke WinAPI (`User32`) для мониторинга процессов и закрытия диалогов

Запуск на Linux/macOS не поддерживается.

## Требования к инфраструктуре

- **PostgreSQL 15+** — доступный по сети для Server и всех Worker-ов
- **Docker** (рекомендуется для PostgreSQL) или установленный PostgreSQL сервер
- **Qodana** (рекомендуется) — статический анализ кода (линтер)

## Структура решения

Решение состоит из **4 проектов** (solution file: `TelegramBot.slnx`). BimLib — не отдельный проект, а директория внутри Worker (`TelegramBot.Worker/BimLib/`).

```
TelegramBot.Core   ←──  TelegramBot.Data
       ↑                       ↑
       ├──── TelegramBot.Server ──┘
       │
       └──── TelegramBot.Worker
                └── BimLib/ (BIM-интеграция)
```

| Проект | Назначение | Зависимости |
|--------|-----------|-------------|
| `TelegramBot.Core` | Модели, DTO, интерфейсы, конфигурация, константы | Нет (без Telegram SDK) |
| `TelegramBot.Data` | PostgreSQL persistence через Dapper + Npgsql | Core |
| `TelegramBot.Server` | Telegram инфраструктура, сервисы, хендлеры, хостинг, helpers | Core + Data |
| `TelegramBot.Worker` | Фоновое выполнение задач (Revit, Navisworks, AI) + BimLib | Core + Data |

### Дерево проекта

```
TelegramBot/
├── TelegramBot.slnx
├── TelegramBot.Core/
│   ├── Config/           # BotOptions, FileSystemOptions
│   ├── Constants/        # ButtonTexts, CallbackPrefixes, CommandCodes
│   ├── DTOs/             # MessageDto, CallbackQueryDto, ButtonDto
│   ├── Extensions/       # ValidationExtensions
│   ├── Interfaces/       # ICommandAppService, IDataService, ICallbackDispatcher, ISessionManager
│   └── Models/           # BotUser, UserSession, PendingCommand, ParsedCallback
├── TelegramBot.Data/
│   ├── DatabaseInitializer.cs
│   ├── PostgresDataService.cs
│   └── Sql/              # SQL-запросы, разбитые по сущностям
│       ├── Queries.Schema.cs
│       ├── Queries.Users.cs
│       ├── Queries.Sessions.cs
│       └── Queries.Commands.cs
├── TelegramBot.Server/
│   ├── Config/           # BotCommandsSetup
│   ├── Constants/        # HandlerPriorities
│   ├── Extensions/       # DependencyInjectionExtensions
│   ├── Helpers/          # MarkdownHelper (унифицированное экранирование)
│   ├── Interfaces/       # IKeyboardBuilder, ISlashCommandService, ITelegramOutputService
│   ├── Properties/       # launchSettings
│   ├── Services/
│   │   ├── Application/  # CommandAppService, SlashCommandService, SessionManager, CallbackDispatcher
│   │   │   └── Handlers/ # Callback-хендлеры (Chain of Responsibility)
│   │   └── Infrastructure/
│   │       ├── FileSystem/    # FileSystemBrowser
│   │       └── Telegram/      # TelegramBotHostedService, TelegramOutputService, KeyboardBuilder
│   ├── Program.cs
│   └── appsettings.json
├── TelegramBot.Worker/
│   ├── BimLib/
│   │   ├── Config/       # BimIntegrationOptions
│   │   ├── Interfaces/   # IRevitVersionDetector, INavisworksPathResolver
│   │   ├── Models/       # RevitDetectedVersion, RevitProcessHealth
│   │   ├── Monitor/      # DialogDismisser, RevitProcessTracker, NavisworksProcessTracker,
│   │   │                 # ProcessHealthHelper, WindowInfo, WindowUtil
│   │   ├── Native/       # P/Invoke WinAPI (User32, Win32Consts)
│   │   └── Services/     # RevitVersionDetector, RevitPathResolver, NavisworksPathResolver
│   ├── Services/
│   │   ├── CommandExecutionService.cs  # polling очереди + выполнение задач
│   │   └── BimLibLogFilter.cs          # Фильтр логов для BimLib
│   ├── Program.cs
│   └── appsettings.json
├── Docs/
├── scripts/
├── Dockerfile
├── .editorconfig
└── README.md
```

## Архитектура

### Поток обработки запроса (Server)

```
Telegram API
     ↓
TelegramBotHostedService (polling, BackgroundService)
     ↓
TelegramUpdateMapper (Update → MessageDto | CallbackQueryDto)
     ↓
CommandAppService
     ├── HandleUserCommandAsync (текстовые команды)
     │    ├── /start, /help → SlashCommandService (без проверки доступа)
     │    └── /export, /automation, /status → SlashCommandService (требуется Approved)
     └── HandleCallbackAsync (inline-клавиатуры)
          ↓
     CallbackDispatcher (Chain of Responsibility)
          ↓
     ICallbackHandler (первый подходящий по приоритету)
```

### Поток выполнения задач (Server → PostgreSQL → Worker)

```
Server (создание сессии)
     │
     ├── INSERT INTO Commands (Status='pending') ──► PostgreSQL
     │                                              │
     │                                    NOTIFY new_tasks
     │                                              │
                                     │              ▼
                          ┌──────────┴──────────┐
                          ▼                     ▼
                    Worker №1              Worker №N
               (LISTEN new_tasks)    (LISTEN new_tasks)
                          │
                    SELECT ... WHERE Status='pending'
                          │
                    ┌─────┴──────┐
                    │            │
               "PDF"/"DWG"   "NWC"/"CLASHREP"
                    │            │
              Revit.exe    Navisworks.exe
                    │            │
                    └─────┬──────┘
                          │
                    UPDATE Status='Done'/'Failed'
                          │
                         PostgreSQL
```

Worker мгновенно получает уведомление через `LISTEN new_tasks` и начинает обработку. Fallback-polling раз в 5 минут при потере соединения. Worker также скрывает старые неактивные сессии по `Worker:CompletedSessionRetentionDays`.

### BimLib (BIM Integration) — встроен в Worker

BimLib — **Windows-only** набор модулей для BIM-интеграции, расположенный внутри Worker-проекта (`TelegramBot.Worker/BimLib/`). Используется `CommandExecutionService` при выполнении Revit/Navisworks-команд.

**Структура:**

| Папка | Содержимое |
|-------|-----------|
| `Config/` | `BimIntegrationOptions` — минимальная/максимальная версия Revit, путь установки |
| `Interfaces/` | `IRevitVersionDetector`, `INavisworksPathResolver` |
| `Models/` | `RevitDetectedVersion`, `RevitProcessHealth` (статусы: Healthy/NotResponding/Error) |
| `Monitor/` | `RevitProcessTracker`, `NavisworksProcessTracker`, `ProcessHealthHelper`, `DialogDismisser`, `WindowUtil`, `WindowInfo` |
| `Native/` | P/Invoke WinAPI: `User32`, `Win32Consts` |
| `Services/` | `RevitVersionDetector`, `RevitPathResolver`, `NavisworksPathResolver` |

**DI-регистрация:** Сервисы BimLib регистрируются напрямую в `Worker/Program.cs` (без отдельного `AddBimIntegration()`):
```csharp
services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>();
services.AddSingleton<RevitPathResolver>();
services.AddSingleton<RevitProcessTracker>();
services.AddSingleton<DialogDismisser>();
services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>();
services.AddSingleton<NavisworksProcessTracker>();
```
Для работы требуется секция `BimIntegration` в `appsettings.json` Worker-а (см. [Конфигурация](#конфигурация)).

**Пространства имён:**
- `TelegramBot.BimLib.Config`
- `TelegramBot.BimLib.Interfaces`
- `TelegramBot.BimLib.Models`
- `TelegramBot.BimLib.Monitor`
- `TelegramBot.BimLib.Native`
- `TelegramBot.BimLib.Services`

**Важные замечания:**
- BimLib помечена `[SupportedOSPlatform("windows")]` — работает только на Windows.
- OpenMcdf 3.x используется для парсинга OLE Structured Storage (.rvt). API: `RootStorage.OpenRead()` → `root.OpenStream()` → `stream.Read()`.
- Доступ к реестру Windows через `Microsoft.Win32.Registry`.
- Весь P/Invoke находится в `Native/` (User32 для операций с окнами).
- `RevitProcessStatus` содержит только 3 значения: `Healthy`, `NotResponding`, `Error`.
- Удалены интерфейсы, не имевшие потребителей вне BimLib: `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker`.

### DI-регистрация

Все сервисы регистрируются как **Singleton** в `DependencyInjectionExtensions.cs` (Server) или напрямую в `Program.cs` (Worker).

### Ключевые сервисы (Server)

| Сервис | Расположение | Ответственность |
|--------|-------------|-----------------|
| `TelegramBotHostedService` | Server/Services/Infrastructure/Telegram | Точка входа, polling-цикл, очистка старых сообщений |
| `CommandAppService` | Server/Services/Application | Центральный диспетчер, проверка доступа |
| `SlashCommandService` | Server/Services/Application | Обработка `/export`, `/automation`, `/status`, `/start`, `/help` |
| `CallbackDispatcher` | Server/Services/Application | Chain-of-responsibility маршрутизация callback-ов |
| `SessionManager` | Server/Services/Application | In-memory сессии (`ConcurrentDictionary`, 5 мин timeout, автоочистка) |
| `FileSystemBrowser` | Server/Services/Infrastructure/FileSystem | Построение inline-клавиатур для навигации по папкам |
| `KeyboardBuilder` | Server/Services/Infrastructure/Telegram | Контекстно-зависимые клавиатуры (команды, файлы, сессии) |
| `TelegramOutputService` | Server/Services/Infrastructure/Telegram | Отправка/редактирование сообщений с retry (429) |
| `TelegramUpdateMapper` | Server/Services/Infrastructure/Telegram | Маппинг `Update` → `MessageDto` / `CallbackQueryDto` |
| `PostgresDataService` | TelegramBot.Data | Вся работа с БД через Dapper + Npgsql |

### Ключевые сервисы (BimLib — внутри Worker)

| Сервис | Расположение | Ответственность |
|--------|-------------|-----------------|
| `RevitVersionDetector` | Worker/BimLib/Services | Определение версии Revit по .rvt-файлу (OLE BasicFileInfo через OpenMcdf) |
| `RevitPathResolver` | Worker/BimLib/Services | Поиск Revit.exe через реестр Windows (HKLM\SOFTWARE\Autodesk\Revit) |
| `NavisworksPathResolver` | Worker/BimLib/Services | Поиск Navisworks.exe/FileConvert.exe через реестр Windows |
| `RevitProcessTracker` | Worker/BimLib/Monitor | Мониторинг здоровья процессов Revit, автозакрытие диалогов |
| `NavisworksProcessTracker` | Worker/BimLib/Monitor | Мониторинг процессов Navisworks (Roamer, FileConvert) |
| `DialogDismisser` | Worker/BimLib/Monitor | Автоматическое закрытие модальных диалогов Revit (#32770) |

### Ключевые сервисы (Worker)

| Сервис | Расположение | Ответственность |
|--------|-------------|-----------------|
| `CommandExecutionService` | TelegramBot.Worker/Services | Polling pending-команд, выполнение Revit/Navisworks/AI, in-memory счётчик batch-а с проверкой финальности по БД, мониторинг здоровья процессов, timeout/lease/crash recovery, автоочистка старых неактивных сессий. Graceful shutdown для внешних процессов не нужен |
| `BimLibLogFilter` | TelegramBot.Worker/Services | Фильтр логов для BimLib-событий (отдельный файл для BIM-специфичных логов) |

### Обработчики callback-ов (Chain of Responsibility)

`CallbackDispatcher` перебирает хендлеры, отсортированные по `Priority` (ниже = раньше), и передаёт callback первому, чей `CanHandle()` вернул `true`.

| Handler | Priority | Префиксы | Назначение |
|---------|----------|----------|------------|
| `AccessRequestHandler` | 0 | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` | Запрос/подтверждение доступа |
| `FileNavigationHandler` | 10 | `GOTOPARENT:` | Навигация по файловой системе |
| `FileSelectionHandler` | 20 | `FILE:` | Выбор файлов/проектов/секций (toggle) |
| `CommandToggleHandler` | 100 | `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:`, `CLASHREP:`, `AUTORES:` | Переключение команд экспорта и автоматизации |
| `SessionManagementHandler` | 100 | `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `CONFIRMDELETESESSION:`, `CONFIRMDELETECOMMAND:` | Управление сессиями, подтверждение удаления и soft-delete команд |
| `CommandSelectionHandler` | 100 | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` | Подтверждение/отмена выбора команд |

## База данных (PostgreSQL)

PostgreSQL-сервер, доступный по сети. Инициализация таблиц при старте через `host.InitializeDatabaseAsync()`.

**Таблицы:**

| Таблица | Назначение | Ключевые поля |
|---------|-----------|---------------|
| `BotUsers` | Пользователи бота | `UserId` (PK), `Username`, `Role` (User/Admin), `Status` (Pending/Approved/Rejected/Blocked), `CreatedAt`, `UpdatedAt` |
| `Sessions` | Сессии пользователей | `SessionId` (PK, SERIAL), `UserId`, `Username`, `Status` (pending/done/Deleted), `FilesAmount`, `CreatedAt`, `UpdatedAt` |
| `Commands` | Команды внутри сессии | `CommandId` (PK, SERIAL), `SessionId` (FK → Sessions), `CommandText`, `FilePath`, `ExecutionOrder`, `Status` (pending/processing/Done/Failed/Deleted), `GUID`, `Lease`, `Priority`, `RetryCount`, `NextRetryAt` |
Soft-delete — строки никогда не удаляются физически (статус `Deleted`).

### Механизм очереди задач

Worker получает команды через PostgreSQL LISTEN/NOTIFY:

1. **Server** после подтверждения выбора создаёт `Sessions` и `Commands` со статусом `pending` и отправляет `NOTIFY new_tasks`
2. **Worker** слушает канал `new_tasks` и мгновенно реагирует на уведомление, вызывая `ClaimPendingCommandsAsync`
3. `FOR UPDATE SKIP LOCKED` позволяет нескольким Worker-ам безопасно конкурировать за команды
4. **Fallback-polling** — если соединение потеряно, Worker проверяет очередь раз в 5 минут
5. **Отмена/удаление команд** — пользователь через `/status` → кнопку «⛔ Отменить» или «🗑»; Server сначала показывает подтверждение, затем мягко удаляет команду (`Status = 'Deleted'`). Worker не выбирает удалённые команды, а `UpdateStatus` не перезаписывает `Deleted`.
6. **Уведомление о завершении** — после завершения всей сессии Worker шлёт `command_completed` через PostgreSQL `NOTIFY`, а Server (`CommandNotificationService`) отправляет пользователю сводку с длительностью сессии и списком ошибочных файлов.

Несколько Worker-ов могут работать параллельно (competing consumers) — каждый берёт следующую команду из очереди.

## Команды бота

Устанавливаются при старте через `BotCommandsSetup.ConfigureAsync()`:

| Команда | Описание |
|---------|----------|
| `/start` | Начало работы, регистрация, запрос доступа |
| `/export` | Меню выбора команд экспорта |
| `/automation` | Меню команд автоматизации |
| `/status` | Глобальный просмотр всех сессий (с `[username]`), управление командами и сессиями |
| `/help` | Справка по командам |

### Базовый флоу работы

```
/start → регистрация → запрос доступа → администратор подтверждает
     ↓
/export → выбор команд (PDF/DWG/NWC/IFC) → APPLYCOMMANDS
     ↓
навигация по папкам (проект → секция) → выбор .rvt-файлов
     ↓
APPLYFILES → дневной лимит файлов → создание сессии + команд в БД → NOTIFY new_tasks → Worker выполняет мгновенно
     ↓
/status → глобальный просмотр всех сессий (с `[username]`) → SESSIONDETAILS → DELETECOMMAND / DELETESESSION → подтверждение
     ↓
⛔ Отменить → DELETECOMMAND → soft-delete в БД (`Status = 'Deleted'`)
```

Аналогичный флоу для `/automation` (BIMDOC/CLASHREP/AUTORES).

## Конфигурация

### appsettings.json (коммитится)

Server:
```json
{
  "Serilog": {
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "Seq", "Args": { "serverUrl": "http://localhost:5341" } }
    ]
  },
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"
  },
  "RateLimit": {
    "MaxRequests": 30,
    "WindowSeconds": 60,
    "MaxFilesPerUserPerDay": 100
  },
  "FileSystem": {
    "RvtDirectoryName": "01_RVT",
    "ProjectDirectoryName": "01_PROJECT",
    "RevitFileExtension": ".rvt",
    "SectionFolderPattern": "^(\\d{2}|\\d{3}|I{1,3})_"
  }
}
```

Worker:
```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"
  },
  "BimIntegration": {
    "MinSupportedVersion": 2018,
    "MaxSupportedVersion": 2026,
    "RevitInstallRoot": "C:\\Program Files\\Autodesk"
  },
  "Worker": {
    "ProcessTimeoutSeconds": 10800,
    "CompletedSessionRetentionDays": 30
  }
}
```

### appsettings.Local.json (gitignored)

```json
{
  "TelegramBot": {
    "Token": "ВАШ_ТОКЕН_БОТА",
    "AdminUserIds": [ 123456789 ]
  },
  "FileSystem": { "RootPath": "B:\\" }
}
```

### Все параметры конфигурации

| Секция | Переменная окружения | Описание |
|--------|---------------------|----------|
| `TelegramBot:Token` | `TelegramBot__Token` | Токен бота (обязательно, валидируется) |
| `TelegramBot:AdminUserIds:0` | `TelegramBot__AdminUserIds__0` | ID администратора Telegram |
| `FileSystem:RootPath` | `FileSystem__RootPath` | Корневая директория для навигации (обязательно, валидируется) |
| `FileSystem:RvtDirectoryName` | — | Имя папки с RVT-файлами (по умолчанию `01_RVT`) |
| `FileSystem:ProjectDirectoryName` | — | Имя папки проекта (по умолчанию `01_PROJECT`) |
| `FileSystem:RevitFileExtension` | — | Расширение Revit-файлов (по умолчанию `.rvt`) |
| `FileSystem:SectionFolderPattern` | — | Regex паттерн для папок секций |
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | PostgreSQL connection string (по умолч. `Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres`) |
| `RateLimit:MaxRequests` | — | Максимум текстовых команд в sliding window |
| `RateLimit:WindowSeconds` | — | Размер окна rate limit в секундах |
| `RateLimit:MaxFilesPerUserPerDay` | — | Максимум файлов, которые один пользователь может поставить в очередь за 24 часа; `0` отключает лимит |
| `Worker:ProcessTimeoutSeconds` | — | Максимальное время выполнения одной команды |
| `Worker:CompletedSessionRetentionDays` | — | Через сколько дней Worker мягко удаляет старые сессии без `pending`/`processing`; `0` отключает автоочистку |
| `BimIntegration:MinSupportedVersion` | — | Минимальная версия Revit для поиска в реестре (по умолчанию `2018`) |
| `BimIntegration:MaxSupportedVersion` | — | Максимальная версия Revit для поиска в реестре (по умолчанию `2026`) |
| `BimIntegration:RevitInstallRoot` | — | Корневая папка установки Autodesk Revit (по умолч. `C:\Program Files\Autodesk`) |

Поддерживаются переменные окружения (синтаксис с `__` как разделителем секций).

## История изменений

Полный список структурных улучшений и рефакторингов — в [ROADMAP.md](ROADMAP.md) (разделы v1.0, v1.1, текущий спринт).

## Безопасность

- Доступ через `/start` — пользователи со статусом `Pending` ждут подтверждения администратором
- Администраторы получают уведомление и могут одобрить (`APPROVEUSER:`) или отклонить (`REJECTUSER:`)
- Токен бота хранится в `appsettings.Local.json` или переменной окружения `TelegramBot__Token`
- Пользователи с `Blocked` или `Rejected` статусом не могут использовать бота
- Администраторы (из `AdminUserIds`) автоматически получают статус `Approved` при первом запуске
- Все одобренные пользователи видят в `/status` **все сессии** всех пользователей и могут управлять ими (удалять, отменять)

## Docker

### PostgreSQL

```bash
docker run -d \
  --name telegram-bot-db \
  -e POSTGRES_DB=telegram_bot \
  -e POSTGRES_PASSWORD=postgres \
  -p 5432:5432 \
  postgres:17
```

### Server (Windows-контейнеры)

Windows-контейнеры (nanoserver ltsc2022). Сборка через многостадийный Dockerfile:

```bash
docker build -t telegram-bot-server -f Dockerfile .
docker run --rm telegram-bot-server
```

Подробнее: [README.TOKEN.md](README.TOKEN.md) — настройка токена.
