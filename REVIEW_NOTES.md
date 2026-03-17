# Code Review Notes

Дата: 2026-03-18

## Findings

1. High: небезопасный дефолтный пароль в сидировании БД.
- `SqliteDataService` добавляет пароль `"qwerty123"` при инициализации.
- Файл: `TelegramBotServer/Services/Infrastructure/Persistence/SqliteDataService.cs:89`

2. High: операции сессий/команд без проверки владельца (`userId`).
- Чтение/удаление по `sessionId` и `commandId` выполняется без обязательной фильтрации по пользователю.
- Файлы:
  - `TelegramBotServer/Services/Application/Handlers/SessionManagementHandler.cs:65`
  - `TelegramBotServer/Services/Application/Handlers/SessionManagementHandler.cs:90`
  - `TelegramBotServer/Services/Infrastructure/Persistence/SqliteDataService.cs:353`
  - `TelegramBotServer/Services/Infrastructure/Persistence/SqliteDataService.cs:434`

3. Medium: двойной ответ на callback query.
- Callback отвечается и в handler, и повторно в hosted service.
- Файлы:
  - `TelegramBotServer/Services/Application/Handlers/FileNavigationHandler.cs:70`
  - `TelegramBotServer/Services/Application/Handlers/FileSelectionHandler.cs:63`
  - `TelegramBotServer/Services/Infrastructure/Telegram/TelegramBotHostedService.cs:112`

4. Medium: сообщения без `Username` отбрасываются.
- Telegram username необязателен; такие пользователи не проходят дальше.
- Файл: `TelegramBotServer/Services/Infrastructure/Telegram/TelegramBotHostedService.cs:88`

5. Medium: файловые операции без локальной обработки I/O-исключений.
- Нет точечной обработки `IOException`/`UnauthorizedAccessException`.
- Файлы:
  - `TelegramBotServer/Services/Infrastructure/FileSystem/FileSystemBrowser.cs:37`
  - `TelegramBotServer/Services/Application/Handlers/FileSelectionHandler.cs:188`

6. Low: culture-sensitive нормализация команды.
- Используется `text.ToLower()` вместо `ToLowerInvariant()`.
- Файл: `TelegramBotServer/Services/Application/CommandAppService.cs:49`

## Build Check

- `dotnet build TelegramBotServer/TelegramBotServer.csproj` — успешно, предупреждений: 0, ошибок: 0.
