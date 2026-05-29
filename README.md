# Telegram Bot Server

Telegram-бот для навигации по файловой системе и управления сессиями с системой запроса доступа и ролями (User/Admin).

## Обзор

.NET 8 background service — Telegram-бот с long-polling. Авторизованным пользователям доступно:

- Навигация по файловой системе через inline-клавиатуры
- Выбор файлов для экспорта
- Управление сессиями и командами
- Автоматизация рабочих процессов

## Технологии

- **.NET 8** — целевая платформа
- **Telegram.Bot** — клиент Telegram Bot API
- **SQLite** — хранение данных (Microsoft.Data.Sqlite + Dapper)
- **Serilog** — структурированное логирование

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
| `SessionManagementHandler` | 100 | `SESSIONDETAILS:`, `DELETESESSION:`, `BACKTOSTATUS:` |
| `CommandSelectionHandler` | 100 | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` |

## База данных

**Таблицы:**
- `BotUsers` — пользователи бота (`Role`: User/Admin, `Status`: Pending/Approved/Rejected/Blocked)
- `Sessions` — сессии пользователей
- `Commands` — команды экспорта

Soft-delete — строки никогда не удаляются физически. Файл БД: `botdata.db`.

## Команды бота

| Команда | Описание |
|---------|----------|
| `/start` | Начало работы, запрос доступа |
| `/status` | Статус сессии и выбранных файлов |
| `/export` | Меню экспорта файлов |
| `/automation` | Меню автоматизации |
| `/help` | Справка по командам |

## Конфигурация

`TelegramBot.Server/appsettings.Local.json` (gitignored):

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

| Секция | Переменная окружения | Описание |
|--------|---------------------|----------|
| `TelegramBot:Token` | `TelegramBot__Token` | Токен бота (обязательно) |
| `TelegramBot:AdminUserIds:0` | `TelegramBot__AdminUserIds__0` | ID администратора Telegram |
| `FileSystem:RootPath` | `FileSystem__RootPath` | Корневая директория для навигации |
| `FileSystem:RvtDirectoryName` | — | Имя папки с RVT-файлами (по умолчанию `01_RVT`) |
| `FileSystem:ProjectDirectoryName` | — | Имя папки проекта (по умолчанию `01_PROJECT`) |
| `ConnectionStrings:Sqlite` | `ConnectionStrings__Sqlite` | Путь к файлу БД SQLite |

## Безопасность

- Доступ через `/start` — пользователи со статусом `Pending` ждут подтверждения.
- Администраторы получают уведомление и могут одобрить (`APPROVEUSER:`) или отклонить (`REJECTUSER:`).
- Токен бота хранится в `appsettings.Local.json` или переменной окружения `TelegramBot__Token`.
