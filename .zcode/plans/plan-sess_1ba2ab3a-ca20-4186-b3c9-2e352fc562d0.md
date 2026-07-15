## Цель

Один админ с жёстко прописанным ID в `appsettings`, вместо массива. Сменить админа можно только правкой конфига (без перекомпиляции).

Telegram ID пользователь вставит сам (заглушка `0`, как `BOT_TOKEN` для токена).

## Изменения

### 1. `TelegramBot.Core/Config/BotOptions.cs`
```csharp
public long[] AdminUserIds { get; set; } = [];
```
→
```csharp
public long AdminUserId { get; set; }
```
Одно значение вместо массива.

### 2. `TelegramBot.Server/Program.cs` (стр. 45-50)
```csharp
var adminIds = host.Services.GetRequiredService<IOptions<BotOptions>>().Value.AdminUserIds;
if (adminIds.Length > 0)
{
    await ...UpsertUsersBatchAsync(adminIds, (int)UserRole.Admin, (int)UserAccessStatus.Approved);
}
```
→
```csharp
var adminId = host.Services.GetRequiredService<IOptions<BotOptions>>().Value.AdminUserId;
if (adminId != 0)
{
    await ...UpsertUserAsync(new BotUser
    {
        UserId = adminId,
        Role = UserRole.Admin,
        Status = UserAccessStatus.Approved,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    });
}
```
Один upsert вместо batch. Проверка `!= 0` (значение по умолчанию) — не сидировать, если не задан.

### 3. `TelegramBot.Server/Services/Application/Handlers/AccessRequestHandler.cs` (стр. 19, 81-86)
```csharp
private readonly long[] _adminIds = botOptions.Value.AdminUserIds;
...
foreach (var adminId in _adminIds) { ... }
```
→
```csharp
private readonly long _adminId = botOptions.Value.AdminUserId;
...
if (_adminId != 0)
{
    _=await outputService.SendMessageWithKeyboardAsync(_adminId, ...);
}
```
Отправка одному админу вместо цикла. Если ID не задан — запрос доступа просто теряется (как раньше при пустом массиве).

### 4. `TelegramBot.Server/appsettings.Local.json` (стр. 7)
```json
"AdminUserIds": [ 123456789 ]
```
→
```json
"AdminUserId": 0
```
Заглушка `0` — пользователь сам впишет число.

### Документация (не критично, но по политике — обновить)

`README.md` — 4 места (стр. 41, 112, 137, 227): массив `[123456789]` → одно значение `0`, тексты про «администраторы» → «администратор».

## Что НЕ меняется
- `UserDataService.UpsertUsersBatchAsync` остаётся (используется только из Program.cs — после правки можно оставить как есть, либо удалить. Оставлю — метод публичный, удаление рискованнее).
- `UserAccessStatus`, `UserRole`, `AuthorizationMiddleware` — без правок.
- Логика авторизации — без изменений.

## Проверка
- `dotnet build TelegramBot.slnx` — полная сборка без ошибок
- Тесты не добавляем (по AGENTS.md)