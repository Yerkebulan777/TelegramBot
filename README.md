# Telegram Bot Server

Telegram-бот для навигации по файловой системе и управления сессиями экспорта/автоматизации с системой запроса доступа и ролями (User/Admin).

## Обзор

.NET 10 background service — Telegram-бот с long-polling (webhook-ов нет). Авторизованным пользователям доступно:

- Навигация по файловой системе через inline-клавиатуры
- Выбор RVT-файлов по секциям для экспорта
- Управление сессиями и командами (PDF, DWG, NWC, IFC и др.)
- Автоматизация: BIM-документирование, Clash Reports, AutoResolve
- Запрос доступа и подтверждение администратором

## Технологии

- **.NET 10** — целевая платформа (`net10.0`)
- **Telegram.Bot 22.10.0.1** — клиент Telegram Bot API
- **SQLite** — хранение данных (Microsoft.Data.Sqlite + Dapper 2.1.79)
- **Serilog** — структурированное логирование (Console + Seq)

## Требования к платформе

⚠️ **Windows only** — проект использует Windows-specific API для навигации по файловой системе. Запуск на Linux/macOS не поддерживается (проверка `RuntimeInformation.IsOSPlatform` в `Program.cs`).

## Структура решения

Решение состоит из 3 проектов (solution file: `TelegramBot.slnx`):

```
TelegramBot.Core   ←──  TelegramBot.Data
       ↑                       ↑
       └──── TelegramBot.Server ──┘
```

| Проект | Назначение | Зависимости |
|--------|-----------|-------------|
| `TelegramBot.Core` | Модели, DTO, интерфейсы, конфигурация, константы | Нет (без Telegram SDK) |
| `TelegramBot.Data` | SQLite persistence через Dapper | Core |
| `TelegramBot.Server` | Telegram инфраструктура, сервисы, хендлеры, хостинг, helpers | Core + Data |

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
│   └── Models/           # BotUser, UserSession, ParsedCallback, FileSystemItem
├── TelegramBot.Data/
│   ├── DatabaseInitializer.cs
│   ├── SqliteDataService.cs
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
├── Docs/
├── scripts/
├── Dockerfile
├── .editorconfig
└── README.md
```

## Архитектура

### Поток обработки запроса

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

### DI-регистрация

Все сервисы регистрируются как **Singleton** в `DependencyInjectionExtensions.cs`.

### Ключевые сервисы

| Сервис | Расположение | Ответственность |
|--------|-------------|-----------------|
| `TelegramBotHostedService` | Server/Services/Infrastructure | Точка входа, polling-цикл, очистка старых сообщений |
| `CommandAppService` | Server/Services/Application | Центральный диспетчер, проверка доступа |
| `SlashCommandService` | Server/Services/Application | Обработка `/export`, `/automation`, `/status`, `/start`, `/help` |
| `CallbackDispatcher` | Server/Services/Application | Chain-of-responsibility маршрутизация callback-ов |
| `SessionManager` | Server/Services/Application | In-memory сессии (`ConcurrentDictionary`, 5 мин timeout, автоочистка) |
| `FileSystemBrowser` | Server/Services/Infrastructure | Построение inline-клавиатур для навигации по папкам |
| `KeyboardBuilder` | Server/Services/Infrastructure | Контекстно-зависимые клавиатуры (команды, файлы, сессии) |
| `TelegramOutputService` | Server/Services/Infrastructure | Отправка/редактирование сообщений с retry (429) |
| `TelegramUpdateMapper` | Server/Services/Infrastructure | Маппинг `Update` → `MessageDto` / `CallbackQueryDto` |
| `SqliteDataService` | Data | Вся работа с БД через Dapper |

### Обработчики callback-ов (Chain of Responsibility)

`CallbackDispatcher` перебирает хендлеры, отсортированные по `Priority` (ниже = раньше), и передаёт callback первому, чей `CanHandle()` вернул `true`.

| Handler | Priority | Префиксы | Назначение |
|---------|----------|----------|------------|
| `AccessRequestHandler` | 0 | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` | Запрос/подтверждение доступа |
| `FileNavigationHandler` | 10 | `GOTOPARENT:` | Навигация по файловой системе |
| `FileSelectionHandler` | 20 | `FILE:`, `APPLYFILES:`, `CANCELFILESEL:` | Выбор файлов/проектов/секций |
| `ExportCommandHandler` | 100 | `PDF:`, `DWG:`, `NWC:`, `IFC:` | Переключение команд экспорта (`CommandToggleHandlerBase`) |
| `AutomationCommandHandler` | 100 | `BIMDOC:`, `CLASHREP:`, `AUTORES:` | Переключение команд автоматизации (`CommandToggleHandlerBase`) |
| `SessionManagementHandler` | 100 | `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `BACKTOSTATUS:` | Управление сессиями |
| `CommandSelectionHandler` | 100 | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` | Подтверждение/отмена выбора команд |

## База данных (SQLite)

Файл БД: `botdata.db`. Инициализация при старте через `host.InitializeDatabaseAsync()`.

**Таблицы:**

| Таблица | Назначение | Ключевые поля |
|---------|-----------|---------------|
| `BotUsers` | Пользователи бота | `UserId` (PK), `Username`, `Role` (User/Admin), `Status` (Pending/Approved/Rejected/Blocked), `CreatedAt`, `UpdatedAt` |
| `Sessions` | Сессии пользователей | `SessionId` (PK, auto), `UserId`, `Username`, `PriorityId`, `Status`, `FilesAmount`, `CreatedAt`, `UpdatedAt` |
| `Commands` | Команды внутри сессии | `CommandId` (PK, auto), `SessionId` (FK), `CommandText`, `FilePath`, `ExecutionOrder`, `Status`, `GUID`, `Lease` |
| `TrackedMessages` | Отслеживаемые сообщения для очистки | `UserId` + `MessageId` (composite PK) |

Soft-delete — строки никогда не удаляются физически (статус `Deleted`).

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
APPLYFILES → создание сессии + команд в БД
     ↓
/status → просмотр очереди → SESSIONDETAILS → DELETECOMMAND / DELETESESSION
```

Аналогичный флоу для `/automation` (BIMDOC/CLASHREP/AUTORES).

## Конфигурация

### appsettings.json (коммитится)

Содержит Serilog (Console + Seq на localhost:5341) и опции файловой системы:

```json
{
  "Serilog": {
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "Seq", "Args": { "serverUrl": "http://localhost:5341" } }
    ]
  },
  "FileSystem": {
    "RvtDirectoryName": "01_RVT",
    "ProjectDirectoryName": "01_PROJECT",
    "RevitFileExtension": ".rvt",
    "SectionFolderPattern": "^(\\d{2}|\\d{3}|I{1,3})_"
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
  "FileSystem": { "RootPath": "B:\\" },
  "ConnectionStrings": { "Sqlite": "Data Source=botdata.db" }
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
| `ConnectionStrings:Sqlite` | `ConnectionStrings__Sqlite` | Путь к файлу БД SQLite (по умолч. `Data Source=botdata.db`) |

Поддерживаются переменные окружения (синтаксис с `__` как разделителем секций).

## Структурные улучшения

В ходе рефакторинга проекта были выполнены следующие изменения:

1. **SQL-запросы разбиты по сущностям** — монолитный `SqlQueries.cs` заменён на папку `TelegramBot.Data/Sql/` с 5 partial-файлами (`Schema`, `Users`, `Sessions`, `Commands`, `TrackedMessages`).
2. **CommandCodes выделен** — из `CallbackPrefixes.cs` вынесен в отдельный файл `TelegramBot.Core/Constants/CommandCodes.cs`.
3. **SessionManager перемещён** — из подпапки `Sessions/` на уровень `Services/Application/` (пустая подпапка удалена).
4. **Markdown-экранирование унифицировано** — два приватных метода (`EscapeMarkdown` и `EscapeMarkdownV2`) объединены в `TelegramBot.Server/Helpers/MarkdownHelper.cs`. Попутно исправлен баг с экранированием обратного слеша.
5. **Удалён мусор** — пустая директория `TelegramBot.Tests/`, артефактные файлы `nul` и `Data Source=botdata.db`.
6. **Добавлен `.editorconfig`** — с правилами именования, форматирования и стиля кода.

## Безопасность

- Доступ через `/start` — пользователи со статусом `Pending` ждут подтверждения администратором
- Администраторы получают уведомление и могут одобрить (`APPROVEUSER:`) или отклонить (`REJECTUSER:`)
- Токен бота хранится в `appsettings.Local.json` или переменной окружения `TelegramBot__Token`
- Пользователи с `Blocked` или `Rejected` статусом не могут использовать бота
- Администраторы (из `AdminUserIds`) автоматически получают статус `Approved` при первом запуске

## Docker

Windows-контейнеры (nanoserver ltsc2022). Сборка через многостадийный Dockerfile:

```bash
docker build -t telegram-bot-server -f Dockerfile .
docker run --rm telegram-bot-server
```

Подробнее: `README.TOKEN.md` — настройка токена.
