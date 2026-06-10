# Telegram Bot Server

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)
[![Qodana](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/qodana.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/qodana.yml)

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
| `TelegramBotHostedService` | Server | Polling-цикл, точка входа |
| `CommandAppService` | Server | Центральный диспетчер, проверка доступа, rate limiting |
| `SlashCommandService` | Server | Обработка текстовых команд |
| `AuthorizationMiddleware` | Server | Доступ через `IAccessValidator` |
| `CallbackDispatcher` | Server | Chain-of-responsibility маршрутизация callback-ов |
| `SessionManager` | Server | In-memory сессии (5 мин timeout) |
| `FileSystemBrowser` | Server | Навигация по файловой системе |
| `KeyboardBuilder` | Server | Построение inline/reply-клавиатур |
| `RateLimiter` | Core | Sliding window per-user (ConcurrentDictionary + Queue) |
| `CommandDataService` / `SessionDataService` / `UserDataService` / `MessageTrackingDataService` | Data | Вся работа с БД (разделение по сущностям) |
| `CommandExecutionService` | Worker | LISTEN/NOTIFY + fallback polling, выполнение Revit/Navisworks/AI |
| `CommandNotificationService` | Server | LISTEN command_completed → Channel<NotificationItem> |
| `NotificationSenderService` | Server | Отправка уведомлений из канала в Telegram |
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
Server создаёт Session + Commands (Status='pending') → PostgreSQL NOTIFY new_tasks
    → Worker LISTEN new_tasks (мгновенно) + fallback polling (5 мин)
    → CLAIM (FOR UPDATE SKIP LOCKED) → выполнение → UPDATE Status='Done'/'Failed'
    → NOTIFY command_completed
    → CommandNotificationService (Server) слушает → Channel<NotificationItem>
    → NotificationSenderService (Server) шлёт сводку пользователю
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
# PostgreSQL (через docker-compose)
docker compose up -d
```

## Безопасность

- Пользователи со статусом `Pending` ждут подтверждения администратором
- Администраторы (из `AdminUserIds`) автоматически получают `Approved`
- Все `Approved` видят **все сессии** всех пользователей через `/status`
- Токен хранится в `appsettings.Local.json` или переменной окружения

## CI/CD

### Непрерывная интеграция (CI)

Каждый push в `main`/`develop` и каждый PR в `main` автоматически:

1. Собирает все проекты на **Windows** (`windows-latest`) — Worker/BimLib требуют WinAPI
2. Проверяет code style через `dotnet format --verify-no-changes`
3. Публикует Server и Worker как артефакты сборки

### Непрерывная доставка (CD) — инструкция по настройке

Для автоматического деплоя на сервер добавьте следующий job в `.github/workflows/ci.yml`
после `build`:

```yaml
  deploy:
    needs: build
    runs-on: windows-latest
    if: github.ref == 'refs/heads/main'
    environment: production

    steps:
    - name: Download artifacts
      uses: actions/download-artifact@v4
      with:
        name: telegram-bot-build
        path: ./publish

    - name: Deploy Server
      run: |
        # Остановить текущий сервер
        # Скопировать файлы из ./publish/server на сервер
        # Запустить сервер
      env:
        DEPLOY_HOST: ${{ secrets.DEPLOY_HOST }}
        DEPLOY_USER: ${{ secrets.DEPLOY_USER }}
        DEPLOY_KEY: ${{ secrets.DEPLOY_SSH_KEY }}

    - name: Deploy Worker
      run: |
        # Остановить текущий worker
        # Скопировать файлы из ./publish/worker на сервер
        # Запустить worker
      env:
        DEPLOY_HOST: ${{ secrets.DEPLOY_HOST }}
        DEPLOY_USER: ${{ secrets.DEPLOY_USER }}
        DEPLOY_KEY: ${{ secrets.DEPLOY_SSH_KEY }}
```

**Необходимые secrets** (настроить в `Settings → Secrets and variables → Actions`):

| Secret | Описание |
|--------|----------|
| `DEPLOY_HOST` | IP/хост Windows-сервера |
| `DEPLOY_USER` | Пользователь для подключения |
| `DEPLOY_SSH_KEY` | SSH-ключ для аутентификации |
| `TelegramBot__Token` | Токен бота (или через `appsettings.Local.json` на сервере) |

**Варианты деплоя:**

- **PowerShell Remoting** — `Invoke-Command -ComputerName $env:DEPLOY_HOST -ScriptBlock { ... }` (нативно для Windows)
- **SSH** — если на сервере установлен OpenSSH Server (настраивается через `Add-WindowsCapability -Online -Name OpenSSH.Server`)
- **Self-hosted runner** — установите GitHub Actions runner непосредственно на сервере, тогда деплой = копирование файлов локально

**Рекомендация:** использовать self-hosted runner на Windows Server — это устраняет необходимость в SSH/PowerShell Remoting и позволяет деплоить атомарно (остановка → копирование → запуск).

## История изменений

См. [ROADMAP.md](ROADMAP.md).
