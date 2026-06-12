# Telegram Bot Server

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)
[![Qodana](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/code_quality.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/code_quality.yml)

Telegram-бот для навигации по файловой системе и управления сессиями экспорта/автоматизации. Задачи выполняются асинхронно через Worker-процесс с PostgreSQL-очередью (LISTEN/NOTIFY + fallback polling).

## Документация

| Документ | Описание |
|----------|----------|
| [AGENTS.md](AGENTS.md) | Архитектура, BimLib, DI, code style, константы |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | Алгоритм выполнения команд, SQL-запросы, схема БД |
| [Docs/BimPluginContract.md](Docs/BimPluginContract.md) | Контракт Revit AddIn, Navisworks/FileConvert и AI-исполнителей |
| [Docs/CriticalReview.md](Docs/CriticalReview.md) | Открытые архитектурные проблемы и узкие места |

## Обзор

.NET 10 background service с long-polling, PostgreSQL-очередью и отдельным Worker для выполнения BIM/AI-задач. Доступные функции:

- Навигация по файловой системе через inline-клавиатуры (проекты → разделы → файлы)
- **Экспорт** (`/export`): `PDF`, `DWG`, `NWC`, `IFC`
- **Автоматизация** (`/automation`): `BIMDOC` (BIM-документирование), `CLASHREP` (Clash Reports), `AUTORES` (AutoResolve)
- Управление сессиями через `/status`: фильтры (Все / Активные / Завершённые / С ошибками), пагинация, удаление сессий и отдельных команд
- Уведомления о завершении сессий: Worker идемпотентно публикует `command_completed`, Server собирает сводку из PostgreSQL и отправляет в Telegram
- Индикатор «печатает…» во время сбора файлов
- Дневной лимит файлов на пользователя (`RateLimit:MaxFilesPerUserPerDay`)
- Дедупликация Revit-файлов по префиксу имени и числовым токенам
- Запрос доступа с подтверждением администратором
- Умный retry: классификация ошибок (`InvalidFileError` → сразу Failed, `ProcessCrashError` → retry с экспоненциальной задержкой)
- Health check endpoints: `/health/live`, `/health/ready`, `/health`; Worker добавляет checks `bimInstallRoot` и `activeProcesses`
- Декомпозиция выполнения команд: `PartitionPoolManager` (приоритетные семафоры), `CommandPreparer` (валидация + BIM-резолвинг), `ProcessRunner` (запуск + timeout + retry), `SessionCompletionTracker` (in-memory счётчик + DB confirmation)

## Технологии

.NET 10, Telegram.Bot 22.x, PostgreSQL (Npgsql + Dapper), Serilog (Console + Seq + rolling file), OpenMcdf (OLE-потоки .rvt/.rfa).

⚠️ **Windows only** — использует Windows Registry и P/Invoke WinAPI.

## Команды бота

| Команда | Описание |
|---------|----------|
| `/start` | Регистрация, запрос доступа |
| `/export` | Меню экспорта (`PDF`/`DWG`/`NWC`/`IFC`) |
| `/automation` | Меню автоматизации (`BIMDOC`/`CLASHREP`/`AUTORES`) |
| `/status` | Просмотр и управление сессиями с фильтрами и пагинацией |
| `/help` | Справка |

Сценарий работы `/export` или `/automation`:
1. Выбор команд из inline-клавиатуры
2. Навигация по проектам (уровень `RootPath`)
3. Подтверждение проекта → переход к `01_PROJECT/<project>/<01_PROJECT>/<раздел>/`
4. Мульти-выбор разделов (папки, подходящие под `SectionFolderPattern`)
5. Worker сканирует `01_RVT` внутри разделов, дедуплицирует, фильтрует по размеру (>50MB) и формату имени
6. `Confirm` → вставка в `Commands` + `pg_notify('new_tasks', correlationId)` → Worker забирает пачку

## Health Check Endpoints

Оба приложения (Server и Worker) предоставляют HTTP-endpoints для мониторинга:

| Endpoint | Server | Worker | Описание |
|----------|--------|--------|----------|
| `GET /health/live` | :5000 | :5001 | Liveness — процесс жив (всегда 200) |
| `GET /health/ready` | :5000 | :5001 | Readiness — проверка PostgreSQL (200 или 503) |
| `GET /health` | :5000 | :5001 | Подробный JSON-отчёт |

Пример ответа `/health`:
```json
{
  "status": "healthy",
  "service": "TelegramBot.Server",
  "timestamp": "2026-06-12T12:00:00Z",
  "uptime": "2d 14h 30m",
  "version": "1.0.0.0",
  "checks": [
    { "name": "database", "status": "healthy" },
    { "name": "process", "status": "healthy" },
    { "name": "notificationChannel", "status": "healthy" }
  ]
}
```

Worker добавляет в `/health` две BIM-проверки:

| Check | Описание | Unhealthy |
|-------|----------|-----------|
| `bimInstallRoot` | Проверяет существование `BimIntegration:RevitInstallRoot` | Директория не найдена |
| `activeProcesses` | Показывает количество активных внешних процессов Worker | Всегда `healthy` (информационно) |

Server добавляет `notificationChannel` — проверяет, что `Channel<NotificationItem>` не закрыт.

Отдельных `/debug/*` endpoints и Prometheus exporter в текущем коде нет.

## BIM-плагины

Worker запускает внешние исполнители и обменивается с ними через JSON-файлы `TaskFile`/`ResultFile`.

| Команды | Исполнитель | stdout/stderr | Важное |
|---------|-------------|---------------|--------|
| `PDF`, `DWG`, `IFC`, `BIMDOC` | `Revit.exe` + установленный Revit AddIn | GUI, нет вывода | Без AddIn Revit просто откроется как GUI и команда завершится таймаутом. Worker определяет версию файла через OLE-стрим `BasicFileInfo` (OpenMcdf) и ищет соответствующий `Revit.exe` в реестре Windows |
| `NWC`, `CLASHREP` | `FileConvert.exe` или `Roamer.exe`/`Navisworks.exe` | Обычно есть | Для полноценного результата нужна обёртка/плагин, который пишет `ResultFile`; иначе Worker использует exit code |
| `AUTORES` | `python ai_agent.py` | Консольный скрипт | Скрипт должен читать `--task`/`--result` и писать `ResultFile` |

Полный контракт описан в [Docs/BimPluginContract.md](Docs/BimPluginContract.md).

## Конфигурация

### Обязательные параметры

| Параметр | Описание |
|----------|----------|
| `TelegramBot:Token` | Токен бота (или env `TelegramBot__Token`) |
| `TelegramBot:AdminUserIds` | Массив ID администраторов (long) |
| `FileSystem:RootPath` | Корневая директория навигации (должна существовать) |
| `ConnectionStrings:Postgres` | PostgreSQL connection string |

### Server — `TelegramBot.Server/appsettings.json`

| Секция | Поле | Описание |
|--------|------|----------|
| `Serilog` | — | Console + Seq (по умолчанию `http://localhost:5341`) |
| `ConnectionStrings.Postgres` | — | DSN PostgreSQL |
| `RateLimit` | `MaxRequests` / `WindowSeconds` | Скользящее окно rate-limiter (`30/60s` по умолчанию) |
| `RateLimit` | `MaxFilesPerUserPerDay` | Лимит суммарных файлов в сессиях за 24ч (`1000` по умолчанию, `0` отключает) |
| `FileSystem` | `RootPath` | Корневая директория (required, validated) |
| `FileSystem` | `RvtDirectoryName` | Имя папки с RVT внутри раздела (default `01_RVT`) |
| `FileSystem` | `ProjectDirectoryName` | Имя папки проекта (default `01_PROJECT`) |
| `FileSystem` | `RevitFileExtension` | Расширение (default `.rvt`) |
| `FileSystem` | `SectionFolderPattern` | Regex для папок-разделов (default `^(\d{2}|\d{3}\|I{1,3})_`) |
| `FileSystem` | `LogDirectory` | Опционально: путь к логам (default `%USERPROFILE%\Documents\TelegramBot\Logs`) |
| `HealthCheck` | `Port` / `ServiceName` / `CacheSeconds` / `DbCheckTimeoutSeconds` | Health-сервер (default `5000` / `TelegramBot.Server` / `10` / `5`) |

### Worker — `TelegramBot.Worker/appsettings.json`

| Секция | Поле | Описание |
|--------|------|----------|
| `Serilog` | — | Console + Seq + rolling file |
| `DialogDismisser` | `MaxDismissAttempts` / `KnownDialogPatterns` / `CloseButtonTexts` / `ExclusionDialogTitles` | Настройки авто-закрытия модальных окон Revit/Navisworks |
| `FileSystem` | `LogDirectory` | Опционально: путь к логам |
| `BimIntegration` | `MinSupportedVersion` / `MaxSupportedVersion` / `RevitInstallRoot` | Поиск Revit в реестре (default `2018`–`2026`, `C:\Program Files\Autodesk`) |
| `ConnectionStrings.Postgres` | — | DSN PostgreSQL |
| `Worker` | `ProcessTimeoutMinutes` | Общий таймаут команды (default `180` = 3ч; используется также для расчёта Lease `+5min`) |
| `Worker` | `MaxRetries` | Кол-во retry перед `Failed` (default `5`) |
| `Worker` | `RetryDelayBaseSeconds` | Базовая задержка retry × 2^(attempt-1) (default `60`) |
| `Worker` | `PermanentFailureExitCodes` | HashSet exit-кодов, считающихся permanent (default пуст) |
| `Worker` | `FallbackPollingIntervalSeconds` | Polling fallback при потере LISTEN/NOTIFY (default `300` = 5 мин) |
| `Worker` | `CleanupIntervalSeconds` | Интервал фоновой очистки истёкших Lease (default `300`) |
| `Worker` | `HealthCheckIntervalSeconds` | Интервал проверки здоровья активных процессов (default `30`) |
| `Worker` | `CompletedSessionRetentionDays` | Авто-cleanup сессий без active команд старше N дней (`0` отключает; default `30`) |
| `Worker` | `Partitions` | `SortedDictionary<threshold, poolSize>`. Команда попадает в первый threshold ≥ Priority. Default: `{0: 5, 1: 3, 2: 2, 3: 1}` (PDF=5, DWG=3, NWC/IFC/BIMDOC/CLASHREP=2, AUTORES=1) |
| `Worker.Commands` | `PDF` / `DWG` / `IFC` / `BIMDOC` / `NWC` / `CLASHREP` / `AUTORES` | Маппинг `CommandText → {ExecutablePath, ArgumentsTemplate, AllowedExtensions, WorkingDirectory?}` |
| `HealthCheck` | `Port` / `ServiceName` / `CacheSeconds` / `DbCheckTimeoutSeconds` | Health-сервер (default `5001` / `TelegramBot.Worker` / `10` / `5`) |

### Пример `appsettings.Local.json` (gitignored)

```json
{
  "TelegramBot": {
    "Token": "ВАШ_ТОКЕН",
    "AdminUserIds": [ 123456789 ]
  },
  "FileSystem": { "RootPath": "B:\\" }
}
```

### Логирование

Логи пишутся в `%USERPROFILE%\Documents\TelegramBot\Logs\{Project}\log-{date}.txt`
с ежедневной ротацией. Структура директорий:

```
%USERPROFILE%\Documents\TelegramBot\Logs\
├── Server\log-20260612.txt              # Основной лог Server
├── Worker\log-20260612.txt              # Основной лог Worker
└── Worker\BimLib\log-20260612.txt       # BIM-специфичные события (Revit/Navisworks, SourceContext starts with "TelegramBot.Worker.BimLib")
```

**Путь можно изменить** через опциональный параметр `FileSystem:LogDirectory`:

```json
{
  "FileSystem": {
    "RootPath": "B:\\",
    "LogDirectory": "D:\\TelegramBot\\Logs"
  }
}
```

Если `LogDirectory` не задан или `null` — используется дефолтный путь.
Параметр применяется ко всем проектам (Server, Worker, BimLib).

Полный список параметров — `appsettings.json` в проектах Server и Worker.

## Запуск

```bash
# PostgreSQL через Docker
docker compose up -d

# Сборка
dotnet build TelegramBot.slnx

# Запуск Server (терминал 1)
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Запуск Worker (терминал 2, отдельный сервис на той же БД)
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

Server и Worker используют одну и ту же PostgreSQL БД, но **независимые процессы** — могут запускаться на разных машинах.

## CI/CD

GitHub Actions: `dotnet build` + `dotnet format --verify-no-changes` + Qodana на каждый push/PR.
Деплой — через self-hosted runner на Windows Server.
