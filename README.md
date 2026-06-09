# Telegram Bot Server

Telegram-бот для навигации по файловой системе и управления сессиями экспорта/автоматизации. Задачи выполняются асинхронно через Worker-процесс с PostgreSQL-очередью (LISTEN/NOTIFY + fallback polling).

## Документация

| Документ | Описание |
|----------|----------|
| [ROADMAP.md](ROADMAP.md) | Дорожная карта проекта |
| [AGENTS.md](AGENTS.md) | Руководство для AI-агентов |
| [Docs/execution-algorithm.md](Docs/execution-algorithm.md) | Алгоритм выполнения команд |

## Обзор

.NET 10 background service с long-polling. Авторизованным пользователям доступны:

- Навигация по файловой системе и выбор RVT-файлов через inline-клавиатуры
- Экспорт: PDF, DWG, NWC, IFC
- Автоматизация: BIM-документирование, Clash Reports, AutoResolve
- Управление сессиями и командами через `/status`
- Запрос доступа с подтверждением администратором
- Дневной лимит файлов на пользователя

## Технологии

- **.NET 10** (`net10.0`)
- **Telegram.Bot 22.10.0.1**
- **PostgreSQL** (Npgsql + Dapper)
- **Serilog** (Console + Seq)
- **OpenMcdf** — чтение OLE-потоков .rvt/.rfa
- **Windows Registry** — поиск Revit/Navisworks

## Требования

⚠️ **Windows only** — использует Windows Registry и P/Invoke WinAPI.

- **PostgreSQL 15+**
- **Docker** (рекомендуется для PostgreSQL)

## Структура

4 проекта (`TelegramBot.slnx`):

```
TelegramBot.Core   ←──  TelegramBot.Data
       ↑                       ↑
       ├──── TelegramBot.Server ──┘
       │
       └──── TelegramBot.Worker
                └── BimLib/ (BIM-интеграция)
```

| Проект | Назначение |
|--------|-----------|
| `TelegramBot.Core` | Модели, DTO, интерфейсы, константы |
| `TelegramBot.Data` | PostgreSQL persistence (Dapper) |
| `TelegramBot.Server` | Telegram-инфраструктура, хендлеры, хостинг |
| `TelegramBot.Worker` | Фоновое выполнение задач + BimLib |

### Ключевые сервисы

| Сервис | Проект | Роль |
|--------|--------|------|
| `TelegramBotHostedService` | Server | Polling-цикл, точка входа |
| `CommandAppService` | Server | Центральный диспетчер, проверка доступа |
| `SlashCommandService` | Server | Обработка текстовых команд |
| `CallbackDispatcher` | Server | Chain-of-responsibility маршрутизация callback-ов |
| `SessionManager` | Server | In-memory сессии (5 мин timeout) |
| `FileSystemBrowser` | Server | Навигация по файловой системе |
| `PostgresDataService` | Data | Вся работа с БД |
| `CommandExecutionService` | Worker | Polling очереди, выполнение Revit/Navisworks/AI |
| `RevitVersionDetector` | Worker/BimLib | Определение версии Revit по .rvt-файлу |
| `RevitPathResolver` | Worker/BimLib | Поиск Revit.exe через реестр |
| `DialogDismisser` | Worker/BimLib | Автозакрытие диалогов Revit |

## Архитектура

### Поток запроса

```
Telegram API → TelegramBotHostedService → TelegramUpdateMapper
    → CommandAppService → SlashCommandService / CallbackDispatcher
```

### Поток задач

```
Server создаёт Commands (Status='pending') → PostgreSQL NOTIFY new_tasks
    → Worker CLAIM (FOR UPDATE SKIP LOCKED) → выполнение → UPDATE Status='Done'/'Failed'
    → NOTIFY command_completed → Server шлёт сводку пользователю
```

Поддерживается несколько Worker-ов (competing consumers).

## Команды бота

| Команда | Описание |
|---------|----------|
| `/start` | Регистрация, запрос доступа |
| `/export` | Меню экспорта (PDF/DWG/NWC/IFC) |
| `/automation` | Меню автоматизации (BIMDOC/CLASHREP/AUTORES) |
| `/status` | Глобальный просмотр всех сессий и управление ими |
| `/help` | Справка |

## Конфигурация

### Обязательные параметры

| Параметр | Описание |
|----------|----------|
| `TelegramBot:Token` | Токен бота (или `TelegramBot__Token`) |
| `TelegramBot:AdminUserIds` | ID администраторов |
| `FileSystem:RootPath` | Корневая директория для навигации |
| `ConnectionStrings:Postgres` | PostgreSQL connection string |

### Пример appsettings.Local.json (gitignored)

```json
{
  "TelegramBot": {
    "Token": "ВАШ_ТОКЕН",
    "AdminUserIds": [ 123456789 ]
  },
  "FileSystem": { "RootPath": "B:\\" }
}
```

Полный список параметров — см. `appsettings.json` в проектах Server и Worker.

## База данных

| Таблица | Назначение |
|---------|-----------|
| `BotUsers` | Пользователи (роли, статусы доступа) |
| `Sessions` | Сессии пользователей |
| `Commands` | Команды внутри сессии (pending → processing → Done/Failed) |
| `TrackedMessages` | Отслеживание сообщений Telegram |

Soft-delete только — `Status = 'Deleted'`, никогда `DELETE FROM`.

## Запуск

```bash
# Сборка
dotnet build TelegramBot.slnx

# Запуск Server
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Запуск Worker (отдельный терминал)
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

## Docker

```bash
# PostgreSQL
docker run -d \
  --name telegram-bot-db \
  -e POSTGRES_DB=telegram_bot \
  -e POSTGRES_PASSWORD=postgres \
  -p 5432:5432 \
  postgres:17

# Server (Windows-контейнер)
docker build -t telegram-bot-server -f Dockerfile .
docker run --rm telegram-bot-server
```

## Безопасность

- Пользователи со статусом `Pending` ждут подтверждения администратором
- Администраторы (из `AdminUserIds`) автоматически получают `Approved`
- Все `Approved` видят **все сессии** всех пользователей через `/status`
- Токен хранится в `appsettings.Local.json` или переменной окружения

## История изменений

См. [ROADMAP.md](ROADMAP.md).
