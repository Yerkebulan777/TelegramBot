# Telegram Bot Server

Telegram-бот для навигации по файловой системе и управления сессиями экспорта/автоматизации с системой запроса доступа и ролями (User/Admin). Задачи выполняются асинхронно через отдельный Worker-процесс с использованием PostgreSQL LISTEN/NOTIFY.

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

## Технологии

- **.NET 10** — целевая платформа (`net10.0`)
- **Telegram.Bot 22.10.0.1** — клиент Telegram Bot API
- **PostgreSQL** — хранение данных (Npgsql + Dapper 2.1.79)
- **PostgreSQL LISTEN/NOTIFY** — очереди задач для Worker (мгновенная реакция)
- **Serilog** — структурированное логирование (Console + Seq)

## Качество кода

- **dotnet format** — форматирование кода согласно `.editorconfig`.
- **Qodana** — статический анализ и поиск мертвого кода. Подробности в [Docs/qodana-setup.md](Docs/qodana-setup.md).

## Требования к платформе

⚠️ **Windows only** — проект использует Windows-specific API для навигации по файловой системе. Запуск на Linux/macOS не поддерживается (проверка `RuntimeInformation.IsOSPlatform` в `Program.cs`).

## Требования к инфраструктуре

- **PostgreSQL 15+** — доступный по сети для Server и всех Worker-ов
- **Docker** (рекомендуется для PostgreSQL) или установленный PostgreSQL сервер
- **Qodana** (рекомендуется) — статический анализ кода (линтер)

## Структура решения

Решение состоит из 4 проектов (solution file: `TelegramBot.slnx`):

```
TelegramBot.Core   ←──  TelegramBot.Data
       ↑                       ↑
       ├──── TelegramBot.Server ──┘
       │
       └──── TelegramBot.Worker (отдельный процесс)
```

| Проект | Назначение | Зависимости |
|--------|-----------|-------------|
| `TelegramBot.Core` | Модели, DTO, интерфейсы, конфигурация, константы | Нет (без Telegram SDK) |
| `TelegramBot.Data` | PostgreSQL persistence через Dapper + Npgsql | Core |
| `TelegramBot.Server` | Telegram инфраструктура, сервисы, хендлеры, хостинг, helpers | Core + Data |
| `TelegramBot.Worker` | Фоновое выполнение задач (Revit, Navisworks, AI) | Core + Data |

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
│       ├── Queries.TrackedMessages.cs
│       ├── Queries.Users.cs
│       ├── Queries.Sessions.cs
│       └── Queries.Commands.cs
├── TelegramBot.Server/
│   ├── Config/           # BotCommandsSetup
│   ├── Constants/        # HandlerPriorities
│   ├── Extensions/       # DependencyInjectionExtensions
│   ├── Helpers/          # MarkdownHelper (унифицированное экранирование)
│   ├── Interfaces/       # IFileSystemBrowser, IKeyboardBuilder, ISlashCommandService
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
│   ├── Services/
│   │   └── CommandExecutionService.cs    # LISTEN/NOTIFY + выполнение задач
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
     │
     └── NOTIFY new_command ──► PostgreSQL
                                     │
                          ┌──────────┴──────────┐
                          ▼                     ▼
                    Worker №1              Worker №N
                    (LISTEN new_command)   (LISTEN new_command)
                          │
                    conn.WaitAsync() — мгновенное пробуждение
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

Worker автоматически переподключается при потере соединения с PostgreSQL и использует fallback poll (5 мин) на случай, если NOTIFY был потерян.

### DI-регистрация

Все сервисы регистрируются как **Singleton** в `DependencyInjectionExtensions.cs`.

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

### Ключевые сервисы (Worker)

| Сервис | Расположение | Ответственность |
|--------|-------------|-----------------|
| `CommandExecutionService` | TelegramBot.Worker/Services | LISTEN/NOTIFY, выборка pending-команд, выполнение Revit/Navisworks/AI |

### Обработчики callback-ов (Chain of Responsibility)

`CallbackDispatcher` перебирает хендлеры, отсортированные по `Priority` (ниже = раньше), и передаёт callback первому, чей `CanHandle()` вернул `true`.

| Handler | Priority | Префиксы | Назначение |
|---------|----------|----------|------------|
| `AccessRequestHandler` | 0 | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` | Запрос/подтверждение доступа |
| `FileNavigationHandler` | 10 | `GOTOPARENT:` | Навигация по файловой системе |
| `FileSelectionHandler` | 20 | `FILE:`, `APPLYFILES:`, `CANCELFILESEL:` | Выбор файлов/проектов/секций |
| `CommandToggleHandler` | 100 | `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:`, `CLASHREP:`, `AUTORES:` | Переключение команд экспорта и автоматизации |
| `SessionManagementHandler` | 100 | `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `BACKTOSTATUS:` | Управление сессиями |
| `CommandSelectionHandler` | 100 | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` | Подтверждение/отмена выбора команд |

## База данных (PostgreSQL)

PostgreSQL-сервер, доступный по сети. Инициализация таблиц при старте через `host.InitializeDatabaseAsync()`.

**Таблицы:**

| Таблица | Назначение | Ключевые поля |
|---------|-----------|---------------|
| `BotUsers` | Пользователи бота | `UserId` (PK), `Username`, `Role` (User/Admin), `Status` (Pending/Approved/Rejected/Blocked), `CreatedAt`, `UpdatedAt` |
| `Sessions` | Сессии пользователей | `SessionId` (PK, SERIAL), `UserId`, `Username`, `Status` (pending/done/Deleted), `FilesAmount`, `CreatedAt`, `UpdatedAt` |
| `Commands` | Команды внутри сессии | `CommandId` (PK, SERIAL), `SessionId` (FK → Sessions), `CommandText`, `FilePath`, `ExecutionOrder`, `Status` (pending/Done/Failed/Deleted), `GUID`, `Lease` |
| `TrackedMessages` | Отслеживаемые сообщения для очистки | `UserId` + `MessageId` (composite PK) |

Soft-delete — строки никогда не удаляются физически (статус `Deleted`).

### Механизм очереди задач (LISTEN/NOTIFY)

PostgreSQL `LISTEN/NOTIFY` используется для мгновенного уведомления Worker-ов о новых командах:

1. **Server** после `INSERT` команд в БД выполняет `NOTIFY new_command, '<sessionId>'`
2. **Worker** при старте выполняет `LISTEN new_command` и ждёт через `NpgsqlConnection.WaitAsync()`
3. При получении NOTIFY Worker мгновенно просыпается, выбирает pending-команды и выполняет их
4. Если NOTIFY потерян — fallback poll через 5 минут

Несколько Worker-ов могут работать параллельно (competing consumers) — каждый берёт следующую команду из очереди.

## Команды бота

Устанавливаются при старте через `BotCommandsSetup.ConfigureAsync()`:

| Команда | Описание |
|---------|----------|
| `/start` | Начало работы, регистрация, запрос доступа |
| `/export` | Меню выбора команд экспорта |
| `/automation` | Меню команд автоматизации |
| `/status` | Просмотр статуса сессий и команд |
| `/help` | Справка по командам |

### Базовый флоу работы

```
/start → регистрация → запрос доступа → администратор подтверждает
     ↓
/export → выбор команд (PDF/DWG/NWC/IFC) → APPLYCOMMANDS
     ↓
навигация по папкам (проект → секция) → выбор .rvt-файлов
     ↓
APPLYFILES → создание сессии + команд в БД → NOTIFY → Worker выполняет
     ↓
/status → просмотр очереди → SESSIONDETAILS → DELETECOMMAND / DELETESESSION
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

Поддерживаются переменные окружения (синтаксис с `__` как разделителем секций).

## История изменений

Полный список структурных улучшений и рефакторингов — в [ROADMAP.md](ROADMAP.md) (разделы v1.0, v1.1, текущий спринт).

## Безопасность

- Доступ через `/start` — пользователи со статусом `Pending` ждут подтверждения администратором
- Администраторы получают уведомление и могут одобрить (`APPROVEUSER:`) или отклонить (`REJECTUSER:`)
- Токен бота хранится в `appsettings.Local.json` или переменной окружения `TelegramBot__Token`
- Пользователи с `Blocked` или `Rejected` статусом не могут использовать бота
- Администраторы (из `AdminUserIds`) автоматически получают статус `Approved` при первом запуске

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
