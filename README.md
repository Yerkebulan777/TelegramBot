# Telegram Bot Server

Telegram-бот для навигации по файловой системе и управления сессиями с ролевой моделью доступа.

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
cd TelegramBotServer
```

### 2. Конфигурация

Создайте файл `TelegramBotServer/appsettings.Local.json`:

```json
{
  "TelegramBot": {
    "Token": "ВАШ_ТОКЕН_БОТА"
  },
  "FileSystem": {
    "RootPath": "B:\\"
  },
  "ConnectionStrings": {
    "Sqlite": "botdata.db"
  }
}
```

> **Примечание:** `appsettings.Local.json` добавлен в `.gitignore` для защиты секретов.

### 3. Сборка и запуск

```bash
# Сборка
dotnet build TelegramBotServer/TelegramBotServer.csproj

# Запуск
dotnet run --project TelegramBotServer/TelegramBotServer.csproj

# Публикация в Release
dotnet publish TelegramBotServer/TelegramBotServer.csproj -c Release
```

### Альтернатива: переменные окружения

Токен можно передать через переменную окружения:

```bash
# Linux/macOS
export TelegramBot__Token="ВАШ_ТОКЕН"

# Windows PowerShell
$env:TelegramBot__Token="ВАШ_ТОКЕН"

# Windows CMD
set TelegramBot__Token=ВАШ_ТОКЕН
```

## Архитектура

```
Telegram API
     ↓
TelegramBotHostedService (polling)
     ↓
TelegramUpdateMapper (Update → MessageDto | CallbackQueryDto)
     ↓
Authorization check (AuthService + SessionManager)
     ↓
CommandAppService.HandleUserCommandAsync / HandleCallbackAsync
     ↓
CallbackDispatcher → Handlers
```

### Ключевые сервисы

| Сервис | Ответственность |
|--------|-----------------|
| `TelegramBotHostedService` | Точка входа, polling-цикл, `/auth` flow |
| `CommandAppService` | Центральный диспетчер команд и callback-ов |
| `CallbackDispatcher` | Маршрутизация callback-ов по приоритетам |
| `FileSystemBrowser` | Построение inline-клавиатур для навигации |
| `SessionManager` | In-memory сессии (ConcurrentDictionary, 5 мин timeout) |
| `SqliteDataService` | Персистентность данных (SQLite + Dapper) |
| `AuthService` | Whitelist + авторизация по паролю |

### Обработчики callback-ов

| Handler | Префиксы |
|---------|----------|
| `FileNavigationHandler` | `OPENFOLDER:`, `GOTOPARENT:` |
| `FileSelectionHandler` | `FILE:`, `SELMODE:`, `APPLYFILES:`, `CANCELSEL:` |
| `ExportCommandHandler` | `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:` |
| `AutomationCommandHandler` | `CLASHREP:`, `AUTORES:` |
| `SessionManagementHandler` | `Sessiondetails:`, `Deletesession:`, `Backtostatus:` |
| `CommandSelectionHandler` | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` |

## База данных

**Таблицы:**
- `Sessions` — сессии пользователей
- `Commands` — команды экспорта
- `Whitelist` — whitelist пользователей
- `Credentials` — учётные данные

**Особенности:**
- Soft-delete — строки никогда не удаляются физически (status = `"Deleted"`)
- Файл БД: `botdata.db` в директории проекта

## Команды бота

| Команда | Описание |
|---------|----------|
| `/start` | Начало работы, проверка авторизации |
| `/auth` | Авторизация по паролю |
| `/status` | Статус сессии и выбранных файлов |
| `/export` | Меню экспорта файлов |
| `/automation` | Меню автоматизации |
| `/help` | Справка по командам |

## Конфигурация

### appsettings.json

| Секция | Параметр | Описание |
|--------|----------|----------|
| `TelegramBot:Token` | Токен бота (обязательно) |
| `FileSystem:RootPath` | Корневая директория для навигации |
| `FileSystem:RvtDirectoryName` | Имя папки с RVT-файлами (по умолчанию `01_RVT`) |
| `FileSystem:ProjectDirectoryName` | Имя папки проекта (по умолчанию `01_PROJECT`) |
| `ConnectionStrings:Sqlite` | Путь к файлу БД SQLite |

## Разработка

### Форматирование кода

```bash
dotnet format
```

### Структура проекта

```
TelegramBotServer/
├── Config/              # Классы конфигурации
├── DTOs/                # Data Transfer Objects
├── Extensions/          # Методы расширения
├── Interfaces/          # Контракты сервисов
├── Models/              # Модели данных
├── Services/
│   ├── Application/     # Бизнес-логика
│   │   ├── Handlers/    # Обработчики команд и callback-ов
│   │   └── Sessions/    # Управление сессиями
│   └── Infrastructure/  # Инфраструктура
│       ├── FileSystem/  # Работа с файловой системой
│       ├── Persistence/ # Доступ к данным
│       └── Telegram/    # Telegram-интеграция
├── Program.cs           # Точка входа
└── appsettings.json     # Конфигурация
```

### Стиль кодирования

- **Nullable reference types**: включены — аннотируйте nullability (`string?`, `T?`)
- **File-scoped namespaces**: предпочтительны
- **Singleton**: все сервисы регистрируются как Singleton
- **Async**: все async-методы имеют суффикс `Async`
- **Логирование**: структурированное с message templates

## Безопасность

- Пароль по умолчанию: `qwerty123` (рекомендуется изменить)
- Whitelist пользователей управляется через БД
- Токен бота хранится в `appsettings.Local.json` (gitignored)

## Лицензия

См. [LICENSE.txt](LICENSE.txt)
