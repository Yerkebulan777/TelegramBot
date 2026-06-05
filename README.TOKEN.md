# Настройка токена Telegram-бота

Переменная окружения: `TelegramBot__Token` (синтаксис с `__` как разделителем секций).

## Вариант 1: Через переменную окружения (рекомендуется)

### Windows (PowerShell)
```powershell
$env:TelegramBot__Token="ваш_новый_токен_здесь"
dotnet run --project TelegramBot.Server
```

### Windows (cmd)
```cmd
set TelegramBot__Token=ваш_новый_токен_здесь
dotnet run --project TelegramBot.Server
```

### Linux / macOS
```bash
export TelegramBot__Token="ваш_новый_токен_здесь"
dotnet run --project TelegramBot.Server
```

## Вариант 2: Через appsettings.Local.json

Откройте файл `TelegramBot.Server/appsettings.Local.json`:

```json
{
  "TelegramBot": {
    "Token": "ваш_новый_токен_здесь"
  }
}
```

**Важно:** Файл `appsettings.Local.json` добавлен в `.gitignore` и не попадёт в репозиторий.

## Получение токена

1. Откройте [@BotFather](https://t.me/BotFather) в Telegram
2. Создайте нового бота или выберите существующего
3. Скопируйте выданный токен

## Безопасность

- **Никогда не коммитьте токен в репозиторий**
- Используйте `appsettings.Local.json` (gitignored) или переменную окружения `TelegramBot__Token`
- При публикации на сервере используйте переменные окружения вашей хостинг-платформы

---

## Связанные документы

| Документ | Описание |
|----------|----------|
| [Docs/execution-algorithm.md](Docs/execution-algorithm.md) | Алгоритм выполнения команд Worker-ом |
