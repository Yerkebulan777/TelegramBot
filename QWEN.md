# TelegramBot Server — Контекст для Qwen Code

## Обзор проекта

**TelegramBotServer** — это Telegram-бот на .NET 8, работающий как фоновый сервис (Background Service). Бот предназначен для управления файлами Revit (`.rvt`) и автоматизации BIM-процессов через файловую систему.

### Основные возможности

- Навигация по файловой системе через inline-кнопки
- Выбор файлов `.rvt` для экспорта
- Экспорт в форматы: PDF, DWG, NWC, IFC
- Автоматизация BIM-команд: BIMDOC, CLASHREP, AUTORES
- Управление очередью сессий и команд через SQLite
- Авторизация пользователей через whitelist + пароль

### Технологии

- **Фреймворк**: .NET 8 (SDK-style проект)
- **Telegram Bot API**: `Telegram.Bot` v22.7.4
- **База данных**: SQLite (`botdata.db`)
- **ORM/Dapper**: `Dapper` v2.1.66, `Microsoft.Data.Sqlite` v9.0.10, `System.Data.SQLite` v2.0.2
- **Хостинг**: .NET Generic Host с `IHostedService`

---

## Сборка и запуск

```bash
# Сборка
dotnet build TelegramBotServer/TelegramBotServer.csproj

# Запуск (разработка)
dotnet run --project TelegramBotServer/TelegramBotServer.csproj

# Публикация (Release)
dotnet publish TelegramBotServer/TelegramBotServer.csproj -c Release
```

### Конфигурация

- **Файл настроек**: `TelegramBotServer/appsettings.json`
- **Connection String**: Ключ `ConnectionStrings.Sqlite` (по умолчанию `botdata.db` в корне проекта)
- **Токен бота**: Жёстко закодирован в `Program.cs` (строка 22)
- **Пароль по умолчанию**: `qwerty123` (вставляется в БД при первом запуске)

---

## Архитектура

### Структура проекта

```
TelegramBotServer/
├── Config/           # Конфигурационные классы
├── DTOs/             # Data Transfer Objects (MessageDto, CallbackQueryDto)
├── Interfaces/       # Интерфейсы сервисов
├── Models/           # Модели данных (UserSession, Command, SessionCommands, etc.)
├── Services/         # Бизнес-логика
│   ├── AuthService.cs           # Проверка whitelist/пароля
│   ├── CommandAppService.cs     # Диспетчер команд и callback'ов
│   ├── KeyboardBuilder.cs       # Построение inline-клавиатур
│   ├── NavigationService.cs     # Навигация по файловой системе
│   ├── SectionNavigationService.cs
│   ├── SessionManager.cs        # Управление сессиями (in-memory)
│   ├── SqliteDataService.cs     # Работа с SQLite
│   ├── TelegramBotHostedService.cs # Polling-цикл бота
│   ├── TelegramOutputService.cs # Отправка сообщений в Telegram
│   └── TelegramUpdateMapper.cs  # Маппинг Update → DTO
├── appsettings.json
├── Program.cs        # Точка входа, DI-контейнер
└── TelegramBotServer.csproj
```

### Поток запроса

```
Telegram API
    ↓
TelegramBotHostedService (long-polling)
    ↓
TelegramUpdateMapper (Update → MessageDto | CallbackQueryDto)
    ↓
AuthService (проверка авторизации)
    ↓
SessionManager (получение/создание сессии)
    ↓
CommandAppService.HandleUserCommandAsync / HandleCallbackAsync
    ↓
NavigationService / KeyboardBuilder (построение клавиатур)
    ↓
SqliteDataService (сохранение состояния)
```

### Ключевые компоненты

#### `TelegramBotHostedService`
Точка входа для polling. Регистрирует команды бота при старте, обрабатывает входящие updates, маршрутизирует на `/auth` и ввод пароля перед основной обработкой.

#### `CommandAppService`
Центральный диспетчер команд. Обрабатывает:
- Текстовые команды: `/export`, `/automation`, `/status`, `/help`
- Callback-запросы от inline-кнопок
- Многошаговые сценарии через `UserSession.State`

#### `NavigationService`
Построение клавиатур для навигации по файловой системе. Фильтрует:
- Директории: только с именами вида `^(\d{2}|\d{3}|I{1,3})_`
- Файлы: только `.rvt`
Пагинация: 20 элементов на страницу.

#### `SessionManager`
In-memory хранилище сессий (`ConcurrentDictionary<long, UserSession>`). Таймаут неактивности: 5 минут. Сессия хранит:
- Текущий путь
- Выбранные файлы
- Ожидающие команды
- Маппинг токенов путей (PathMap)

#### `SqliteDataService`
Все операции с БД. Таблицы: `Sessions`, `Commands`, `Whitelist`, `Credentials`. Используется soft-delete (статус `Deleted`).

#### `AuthService`
Проверка пользователя по таблице `Whitelist`, валидация пароля против `Credentials` (plain-text).

---

## Протокол callback-данных

Telegram ограничивает callback data 64 байтами. Для обхода используются короткие токены, полные пути хранятся в `UserSession.PathMap[token]`.

### Префиксы callback'ов

| Префикс | Назначение |
|---------|------------|
| `NAV1:`, `NAV2:` | Навигация (внутрь/назад) |
| `FILE:` | Выбор/снятие файла |
| `PREV:`, `NEXT:` | Пагинация списка файлов |
| `SELMODE:` | Переключение режима выбора (файл/секция/проект) |
| `APPLYFILES:`, `CANCELFILESEL:` | Подтверждение/отмена выбора файлов |
| `PDF:`, `DWG:`, `NWC:`, `IFC:` | Выбор формата экспорта |
| `BIMDOC:`, `CLASHREP:`, `AUTORES:` | BIM-команды |
| `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` | Выбор команд |
| `Sessiondetails:`, `Deletesession:`, `Deletecommand:` | Управление статусом/очередью |

---

## Схема базы данных

```sql
Sessions (
    SessionId INTEGER PRIMARY KEY,
    UserId INTEGER,
    Username TEXT,
    PriorityId INTEGER DEFAULT 0,
    Status TEXT DEFAULT 'pending',  -- pending/active/completed/deleted
    CreatedAt TEXT,
    FilesAmount INTEGER,
    UpdatedAt TEXT
)

Commands (
    CommandId INTEGER PRIMARY KEY,
    SessionId INTEGER FOREIGN KEY,
    CommandText TEXT,
    FilePath TEXT,
    ExecutionOrder INTEGER,
    Status TEXT DEFAULT 'pending',  -- pending/running/completed/failed/deleted
    CreatedAt TEXT,
    GUID TEXT,
    Lease INTEGER DEFAULT 3600
)

Whitelist (
    UserId INTEGER PRIMARY KEY,
    Username TEXT,
    Timestamp TEXT
)

Credentials (
    id INTEGER PRIMARY KEY,
    password TEXT  -- plain-text, по умолчанию 'qwerty123'
)
```

---

## Известные проблемы

См. [RACE_CONDITION_ANALYSIS.md](./RACE_CONDITION_ANALYSIS.md) для подробного анализа.

### Критические

1. **Гонки данных в `UserSession`** — мутабельные коллекции (`SelectedFiles`, `PendingCommand`, `PathMap`) изменяются из параллельных обработчиков без синхронизации.
2. **`SessionManager.CleanUpExpiredSessions`** — гонка между проверкой таймаута и обновлением `LastActivity`.
3. **Блокирующие async-вызовы в конструкторах** — `InitializeDatabase().Wait()` в `SqliteDataService`.

### Средние

4. **Транзакции SQLite** — `DeleteSessionAsync` использует raw SQL `BEGIN TRANSACTION` вместо `connection.BeginTransaction()`.
5. **Токен бота в коде** — хардкод в `Program.cs`, а не в конфигурации.

---

## Практики разработки

### Стиль кода

- **Nullable reference types**: включены (`<Nullable>enable</Nullable>`)
- **Implicit usings**: включены (`<ImplicitUsings>enable</ImplicitUsings>`)
- **Async/await**: преобладает async-стиль, но есть нарушения (блокирующие вызовы в конструкторах)

### Тестирование

**Автоматизированные тесты отсутствуют.** В проекте нет тестовых проектов или CI-пайплайнов для тестирования.

### Вклад в проект

1. Следуйте существующей архитектуре (сервисы + интерфейсы)
2. Избегайте мутабельного состояния без синхронизации
3. Используйте async/await последовательно (без `.Wait()` и `.Result`)
4. Добавляйте миграции БД в `InitializeDatabaseAsync()`

---

## Полезные ссылки

- [ARCHITECTURE.md](./ARCHITECTURE.md) — общая архитектура (пустой файл)
- [RACE_CONDITION_ANALYSIS.md](./RACE_CONDITION_ANALYSIS.md) — анализ гонок данных
- [CLAUDE.md](./CLAUDE.md) — руководство для Claude Code
- [LICENSE.txt](./LICENSE.txt) — MIT License
