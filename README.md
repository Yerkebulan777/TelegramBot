# Telegram Bot Server

Telegram-бот для навигации по файловой системе и управления сессиями с системой запроса доступа и ролями (User/Admin).

## Обзор проекта

.NET 8 background service — Telegram-бот с использованием long-polling (без вебхуков). Предоставляет авторизованным пользователям возможность:

- Навигации по файловой системе через inline-клавиатуры
- Выбора файлов для экспорта
- Управления сессиями и командами
- Автоматизации рабочих процессов

## Технологии

- **.NET 8** — целевая платформа
- **Telegram.Bot** — клиент Telegram Bot API
- **SQLite** — хранение данных (Microsoft.Data.Sqlite + Dapper)
- **Serilog** — структурированное логирование

## Требования

- .NET 8 SDK
- Telegram Bot Token (получить у [@BotFather](https://t.me/BotFather))

## Установка и запуск

### 1. Клонирование репозитория

```bash
git clone <repository-url>
cd TelegramBot
```

### 2. Конфигурация

Создайте файл `TelegramBot.Server/appsettings.Local.json`:

```json
{
  "TelegramBot": {
    "Token": "ВАШ_ТОКЕН_БОТА",
    "AdminUserIds": [ 123456789 ]
  },
  "FileSystem": {
    "RootPath": "B:\\"
  }
}
```

> **Примечание:** `appsettings.Local.json` добавлен в `.gitignore` для защиты секретов.
> `AdminUserIds` — числовые ID администраторов Telegram (получаются у [@userinfobot](https://t.me/userinfobot)).
> При запуске администраторы автоматически получают статус `Approved` в таблице `BotUsers`.

### 3. Сборка и запуск

```bash
# Сборка
dotnet build TelegramBot.Server/TelegramBot.Server.csproj

# Запуск
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Публикация в Release
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release
```

### Альтернатива: переменные окружения

Токен и ID администраторов можно передать через переменные окружения:

```bash
# Linux/macOS
export TelegramBot__Token="ВАШ_ТОКЕН"
export TelegramBot__AdminUserIds__0=123456789
export TelegramBot__AdminUserIds__1=987654321

# Windows PowerShell
$env:TelegramBot__Token="ВАШ_ТОКЕН"
$env:TelegramBot__AdminUserIds__0=123456789

# Windows CMD
set TelegramBot__Token=ВАШ_ТОКЕН
set TelegramBot__AdminUserIds__0=123456789
```

## Архитектура

```
Telegram API
     ↓
TelegramBotHostedService (polling)
     ↓
TelegramUpdateMapper (Update → MessageDto | CallbackQueryDto)
     ↓
CommandAppService.HandleUserCommandAsync / HandleCallbackAsync
     ├── /start bypasses access check → registration/help
     └── other commands → checks BotUsers.Status = Approved
              ↓
CallbackDispatcher → Handlers
```

### Ключевые сервисы

| Сервис | Ответственность |
|--------|-----------------|
| `TelegramBotHostedService` | Точка входа, polling-цикл |
| `CommandAppService` | Центральный диспетчер команд и callback-ов, проверка доступа |
| `CallbackDispatcher` | Маршрутизация callback-ов по приоритетам |
| `FileSystemBrowser` | Построение inline-клавиатур для навигации |
| `SessionManager` | In-memory сессии (ConcurrentDictionary, 5 мин timeout) |
| `SqliteDataService` | Персистентность данных (SQLite + Dapper) |
| `AccessRequestHandler` | Обработка запросов доступа (Pending → Approved/Rejected) |

### Обработчики callback-ов

| Handler | Приоритет | Префиксы |
|---------|-----------|----------|
| `AccessRequestHandler` | 0 | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` |
| `FileNavigationHandler` | 10 | `OPENFOLDER:`, `GOTOPARENT:` |
| `FileSelectionHandler` | 20 | `FILE:`, `SELMODE:`, `APPLYFILES:`, `CANCELSEL:` |
| `ExportCommandHandler` | 100 | `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:` |
| `AutomationCommandHandler` | 100 | `CLASHREP:`, `AUTORES:` |
| `SessionManagementHandler` | 100 | `Sessiondetails:`, `Deletesession:`, `Backtostatus:` |
| `CommandSelectionHandler` | 100 | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` |

## База данных

**Таблицы:**
- `BotUsers` — пользователи бота (UserId, Username, `Role`: User/Admin, `Status`: Pending/Approved/Rejected/Blocked)
- `Sessions` — сессии пользователей
- `Commands` — команды экспорта

**Особенности:**
- Soft-delete — строки никогда не удаляются физически (status = `"Deleted"`)
- Файл БД: `botdata.db` в директории проекта

## Команды бота

| Команда | Описание |
|---------|----------|
| `/start` | Начало работы, запрос доступа (Pending → уведомление админам) |
| `/status` | Статус сессии и выбранных файлов |
| `/export` | Меню экспорта файлов |
| `/automation` | Меню автоматизации |
| `/help` | Справка по командам |

## Конфигурация

### appsettings.json / переменные окружения

| Секция | Переменная окружения | Описание |
|--------|---------------------|----------|
| `TelegramBot:Token` | `TelegramBot__Token` | Токен бота (обязательно) |
| `TelegramBot:AdminUserIds:0` | `TelegramBot__AdminUserIds__0` | ID администратора Telegram (можно повторять `__1`, `__2`...) |
| `FileSystem:RootPath` | `FileSystem__RootPath` | Корневая директория для навигации |
| `FileSystem:RvtDirectoryName` | — | Имя папки с RVT-файлами (по умолчанию `01_RVT`) |
| `FileSystem:ProjectDirectoryName` | — | Имя папки проекта (по умолчанию `01_PROJECT`) |
| `ConnectionStrings:Sqlite` | `ConnectionStrings__Sqlite` | Путь к файлу БД SQLite (по умолч. `botdata.db`) |

## Разработка

### Форматирование кода

```bash
dotnet format
```

### Структура проекта

```
TelegramBot.sln
├── TelegramBot.Core/           # Модели, DTO, интерфейсы, конфигурация
│   └── (Models, DTOs, Interfaces, Config)
├── TelegramBot.Data/           # SQLite + Dapper
│   ├── SqliteDataService.cs
│   └── DatabaseInitializer.cs
├── TelegramBot.Server/         # Telegram-инфраструктура, хостинг
│   ├── Services/
│   │   ├── Application/        # Бизнес-логика, хендлеры
│   │   └── Infrastructure/     # Telegram, файловая система
│   ├── Extensions/
│   ├── Program.cs
│   └── appsettings.json
└── TelegramBot.Tests/          # Тесты
```

### Стиль кодирования

- **Nullable reference types**: включены — аннотируйте nullability (`string?`, `T?`)
- **File-scoped namespaces**: предпочтительны
- **Singleton**: все сервисы регистрируются как Singleton
- **Async**: все async-методы имеют суффикс `Async`
- **Логирование**: структурированное с message templates

## Безопасность

- Доступ к боту — по запросу через `/start`. Пользователи со статусом `Pending` ожидают подтверждения администратора.
- Администраторы (`AdminUserIds`) получают уведомление о новом запросе и могут одобрить (`APPROVEUSER:`) или отклонить (`REJECTUSER:`).
- Статусы пользователей: `Pending` → `Approved` | `Rejected`. Статус `Blocked` определён, но не используется.
- Токен бота хранится в `appsettings.Local.json` (gitignored) или переменной окружения `TelegramBot__Token`.
- ID администраторов задаются в конфигурации или через `TelegramBot__AdminUserIds__0`, `TelegramBot__AdminUserIds__1` и т.д.

## Лицензия

См. [LICENSE.txt](LICENSE.txt)
