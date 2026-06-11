# Telegram Bot Server

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)
[![Qodana](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/code_quality.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/code_quality.yml)

Telegram-бот для навигации по файловой системе и управления сессиями экспорта/автоматизации. Задачи выполняются асинхронно через Worker-процесс с PostgreSQL-очередью (LISTEN/NOTIFY + fallback polling).

## Документация

| Документ | Описание |
|----------|----------|
| [AGENTS.md](AGENTS.md) | Архитектура, BimLib, DI, code style, константы |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | Алгоритм выполнения команд, SQL-запросы |
| [Docs/BimPluginContract.md](Docs/BimPluginContract.md) | Контракт Revit AddIn, Navisworks/FileConvert и AI-исполнителей |

## Обзор

.NET 10 background service с long-polling, PostgreSQL-очередью и отдельным Worker для выполнения BIM/AI-задач. Доступные функции:

- Навигация по файловой системе через inline-клавиатуры
- Экспорт: PDF, DWG, NWC, IFC
- Автоматизация: BIM-документирование, Clash Reports, AutoResolve
- Управление сессиями через `/status`
- Уведомления о завершении сессий: Worker идемпотентно публикует `command_completed`, Server собирает сводку из PostgreSQL
- Дневной лимит файлов на пользователя
- Запрос доступа с подтверждением администратором
- Умный retry: классификация ошибок (InvalidFileError → сразу Failed, ProcessCrashError → retry)
- Health check endpoints: `/health/live`, `/health/ready`, `/health`; Worker добавляет checks `bimInstallRoot` и `activeProcesses`

## Технологии

.NET 10, Telegram.Bot 22.x, PostgreSQL (Npgsql + Dapper), Serilog, OpenMcdf (OLE-потоки .rvt/.rfa).

⚠️ **Windows only** — использует Windows Registry и P/Invoke WinAPI.

## Команды бота

| Команда | Описание |
|---------|----------|
| `/start` | Регистрация, запрос доступа |
| `/export` | Меню экспорта (PDF/DWG/NWC/IFC) |
| `/automation` | Меню автоматизации (BIMDOC/CLASHREP/AUTORES) |
| `/status` | Просмотр и управление сессиями |
| `/help` | Справка |

## Health Check Endpoints

Оба приложения (Server и Worker) предоставляют HTTP-endpoints для мониторинга:

| Endpoint | Server | Worker | Описание |
|----------|--------|--------|----------|
| `GET /health/live` | :5000 | :5001 | Liveness — процесс жив |
| `GET /health/ready` | :5000 | :5001 | Readiness — проверка PostgreSQL |
| `GET /health` | :5000 | :5001 | Подробный JSON-отчёт |

Пример ответа `/health`:
```json
{
  "status": "healthy",
  "service": "TelegramBot.Server",
  "timestamp": "2026-06-11T12:00:00Z",
  "uptime": "2d 14h 30m",
  "version": "1.0.0.0",
  "checks": [
    { "name": "database", "status": "healthy" },
    { "name": "process", "status": "healthy" }
  ]
}
```

Worker добавляет в `/health` две BIM-проверки:

| Check | Описание |
|-------|----------|
| `bimInstallRoot` | Проверяет существование `BimIntegration:RevitInstallRoot` |
| `activeProcesses` | Показывает количество активных внешних процессов Worker |

Отдельных `/debug/*` endpoints и Prometheus exporter в текущем коде нет.

## BIM-плагины

Worker запускает внешние исполнители и обменивается с ними через JSON-файлы `TaskFile`/`ResultFile`.

| Команды | Исполнитель | Важное |
|---------|-------------|--------|
| `PDF`, `DWG`, `IFC`, `BIMDOC` | `Revit.exe` + установленный Revit AddIn | Без AddIn Revit просто откроется как GUI и команда завершится таймаутом |
| `NWC`, `CLASHREP` | `FileConvert.exe` или Navisworks/обёртка | Для полноценного результата нужна обёртка/плагин, который пишет `ResultFile`; иначе Worker использует exit code |
| `AUTORES` | `python ai_agent.py` | Скрипт должен читать `--task`/`--result` и писать `ResultFile` |

Полный контракт описан в [Docs/BimPluginContract.md](Docs/BimPluginContract.md).

## Конфигурация

### Обязательные параметры

| Параметр | Описание |
|----------|----------|
| `TelegramBot:Token` | Токен бота (или `TelegramBot__Token`) |
| `TelegramBot:AdminUserIds` | ID администраторов |
| `FileSystem:RootPath` | Корневая директория навигации |
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

### Логирование

Логи пишутся в `%USERPROFILE%\Documents\TelegramBot\Logs\{Project}\log-{date}.txt`
с ежедневной ротацией. Структура директорий:

```
%USERPROFILE%\Documents\TelegramBot\Logs\
├── Server\log-20260611.txt          # Основной лог Server
├── Worker\log-20260611.txt          # Основной лог Worker
└── Worker\BimLib\log-20260611.txt  # BIM-специфичные события Revit/Navisworks
```

**Путь можно изменить** через опциональный параметр `FileSystem:LogDirectory`:

```json
{
  "FileSystem": {
    "RootPath": "B:\\",
    "LogDirectory": "D:\\TelegramBot\\Logs"    // необязательно, по умолчанию — Documents
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

# Запуск Worker (терминал 2)
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

## CI/CD

GitHub Actions: `dotnet build` + `dotnet format --verify-no-changes` + Qodana на каждый push/PR.
Деплой — через self-hosted runner на Windows Server.
