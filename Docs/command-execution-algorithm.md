# Алгоритм выполнения команд (Command Execution Algorithm)

## Обзор

Фоновая служба `TelegramBot.Worker` выполняет команды для Revit/Navisworks/AI через механизм PostgreSQL LISTEN/NOTIFY.

## Архитектура

```
TelegramBot.Server          PostgreSQL          TelegramBot.Worker
       │                        │                        │
       │ 1. Создает сессию      │                        │
       │    и команды           │                        │
       ├───────────────────────>│                        │
       │                        │                        │
       │ 2. INSERT Commands     │                        │
       │    Status='pending'    │                        │
       │───────────────────────>│                        │
       │                        │                        │
       │ 3. NOTIFY new_command  │                        │
       │───────────────────────>│                        │
       │                        │ 4. conn.WaitAsync()    │
       │                        │ (блокировка до NOTIFY) │
       │                        ├───────────────────────>│
       │                        │                        │
       │                        │ 5. SELECT команды      │
       │                        │ WHERE Status='pending' │
       │<───────────────────────┤                        │
       │                        │                        │
       │                        │ 6. Выполнение команды  │
       │                        │ (Revit/Navisworks/AI)  │
       │                        │                        │
       │                        │ 7. UPDATE Status       │
       │                        │ 'Done' | 'Failed'      │
       │───────────────────────>│                        │
       │                        │                        │
```

## Состояния команды

| Статус | Описание |
|--------|----------|
| `pending` | Команда создана, ожидает выполнения |
| `in_progress` | Команда выполняется (опционально) |
| `Done` | Команда успешно завершена |
| `Failed` | Команда завершена с ошибкой |
| `Deleted` | Команда удалена (soft-delete) |

## Алгоритм Worker (CommandExecutionService)

### 1. Инициализация подключения

```csharp
await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();
await conn.ExecuteNonQueryAsync("LISTEN new_command;");
```

### 2. Цикл ожидания команд

```
┌─────────────────────────────────────┐
│  Бесконечный цикл                   │
│  (внутри BackgroundService)         │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│  conn.WaitAsync()                   │
│  (блокируется до NOTIFY или         │
│   таймаута ~60 сек)                 │
└──────────────┬──────────────────────┘
               │
               ▼ (получено уведомление)
┌─────────────────────────────────────┐
│  dataService.GetPendingCommandsAsync│
│  SELECT * FROM Commands             │
│  WHERE Status = 'pending'           │
│  ORDER BY CreatedAt                 │
└──────────────┬──────────────────────┘
               │
               ▼ (команды найдены)
┌─────────────────────────────────────┐
│  Для каждой команды:                │
│  ExecuteOneAsync(command)           │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│  try {                              │
│    // Выполнение по типу команды    │
│    switch (command.Code) {          │
│      case "EXPORT_IFC": ...         │
│      case "EXPORT_DWG": ...         │
│      case "AI_SUMMARY": ...         │
│    }                                │
│                                     │
│    UPDATE Status='Done'             │
│  } catch (Exception ex) {           │
│    LogError(ex)                     │
│    UPDATE Status='Failed'           │
│  }                                  │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│  Продолжить цикл (ждать след. NOTIFY│
└─────────────────────────────────────┘
```

### 3. Обработка ошибок подключения

При потере соединения с PostgreSQL:
1. Логирование ошибки
2. Пауза 5 секунд
3. Попытка переподключения
4. Повторное выполнение `LISTEN new_command`

## Алгоритм Server (создание команды)

### 1. Создание сессии и команд

```csharp
// SlashCommandService.ConfirmFileSelectionAsync()
var sessionId = await dataService.CreateSessionWithCommandsAsync(
    userId,
    selectedFiles,
    commandCodes  // например: ["EXPORT_IFC", "EXPORT_DWG"]
);
```

### 2. INSERT команд в базу

```sql
INSERT INTO "Commands" ("SessionId", "Code", "Status", "CreatedAt")
VALUES (@SessionId, @Code, 'pending', NOW())
RETURNING "Id";
```

### 3. Уведомление Worker

```csharp
await dataService.NotifyNewCommandsAsync();
// Выполняет: NOTIFY new_command;
```

## Ключевые компоненты

| Компонент | Проект | Описание |
|-----------|--------|----------|
| `CommandExecutionService` | TelegramBot.Worker | Основная служба выполнения |
| `PostgresDataService.GetPendingCommandsAsync` | TelegramBot.Data | Выборка pending команд |
| `PostgresDataService.NotifyNewCommandsAsync` | TelegramBot.Data | Отправка NOTIFY |
| `PostgresDataService.UpdateCommandStatusAsync` | TelegramBot.Data | Обновление статуса |
| `Sql/Commands.cs` | TelegramBot.Data | SQL-запросы (5 partial-методов) |

## SQL-запросы

### Получение pending команд

```sql
SELECT c."Id", c."SessionId", c."Code", c."Status", c."CreatedAt",
       s."UserId", s."SelectedFiles"
FROM "Commands" c
JOIN "Sessions" s ON c."SessionId" = s."Id"
WHERE c."Status" = 'pending'
ORDER BY c."CreatedAt";
```

### Обновление статуса

```sql
UPDATE "Commands"
SET "Status" = @Status,
    "CompletedAt" = CASE 
        WHEN @Status IN ('Done', 'Failed') THEN NOW()
        ELSE NULL
    END
WHERE "Id" = @CommandId;
```

### Отправка уведомления

```sql
NOTIFY new_command;
```

## Безопасность и надёжность

1. **Soft-delete**: Команды никогда не удаляются физически, только `Status = 'Deleted'`
2. **Транзакции**: Создание сессии и команд — в одной транзакции
3. **Повторные попытки**: Worker переподключается при потере соединения
4. **Логирование**: Все ошибки логируются с полным контекстом (Id команды, код, UserId)
5. **Изоляция**: Worker и Server работают независимо, общаются только через БД

## Расширение (добавление новой команды)

1. Добавить код команды в `CommandCodes.cs`
2. Добавить SQL-запрос в `Sql/Commands.cs` (если нужен новый)
3. Добавить обработку в `CommandExecutionService.ExecuteOneAsync()` (switch/case)
4. При необходимости — обновить UI в `TelegramBot.Server` (клавиатуры, сообщения)

## Диагностика

### Проверка pending команд

```sql
SELECT COUNT(*) FROM "Commands" WHERE "Status" = 'pending';
```

### Последние выполненные команды

```sql
SELECT "Id", "Code", "Status", "CreatedAt", "CompletedAt"
FROM "Commands"
ORDER BY "CreatedAt" DESC
LIMIT 10;
```

### Проверка LISTEN-подписки

```sql
SELECT * FROM pg_listening_channels();
-- Должен вернуть: new_command
```
