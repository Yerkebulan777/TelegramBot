# Настройка токена Telegram-бота

## Вариант 1: Через переменную окружения (рекомендуется)

### Windows (PowerShell)
```powershell
$env:TELEGRAM_BOT_TOKEN="ваш_новый_токен_здесь"
dotnet run --project TelegramBot.Server
```

### Windows (cmd)
```cmd
set TELEGRAM_BOT_TOKEN=ваш_новый_токен_здесь
dotnet run --project TelegramBot.Server
```

### Linux / macOS
```bash
export TELEGRAM_BOT_TOKEN="ваш_новый_токен_здесь"
dotnet run --project TelegramBot.Server
```

### Через файл .env
1. Откройте файл `.env` в корне проекта
2. Замените `ваш_новый_токен_здесь` на ваш реальный токен бота
3. Запустите проект (переменные из .env будут загружены автоматически при наличии соответствующей настройки)

## Вариант 2: Через appsettings.Local.json

Откройте файл `TelegramBot.Server/appsettings.Local.json` и замените значение `%TELEGRAM_BOT_TOKEN%` на ваш реальный токен.

**Важно:** Файл `appsettings.Local.json` уже добавлен в `.gitignore` и не попадёт в репозиторий.

## Получение токена

1. Откройте @BotFather в Telegram
2. Создайте нового бота или выберите существующего
3. Скопируйте выданный токен

## Безопасность

- **Никогда не коммитьте токен в репозиторий**
- Файл `.env` также добавлен в `.gitignore`
- При публикации на сервере используйте переменные окружения вашей хостинг-платформы
