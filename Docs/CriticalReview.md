# Critical Review — Статус критичных замечаний и остаточные риски

> Этот документ перечисляет архитектурные риски, которые известны команде, но на данный момент
> не устранены (design decision / low priority / требует внешних изменений).

---

## 1. At-least-once Telegram delivery boundary

**Риск:** Если `NotificationSenderService` успешно отправил Telegram-сообщение (`SendMessageAsync`),
но Server упал до того, как outbox-запись помечена `sent`, после restart запись будет повторно
заклеймлена и отправлена снова. Пользователь получит дубликат сводки.

**Статус:** Принятый риск. Telegram не поддерживает идемпотентность отправки, и price of
exactly-once (2PC / transactional outbox + idempotency key) неоправдан для уведомлений.
Дубликаты возможны, но редки — окно уязвимости составляет один HTTP round-trip.

**Решение не принято,** т.к.:
- Дубликат сводки — не критичное событие (пользователь видит ту же информацию повторно)
- Exactly-once потребовал бы distributed transaction между Server и Telegram API

---

## 2. Single-Writer assumption на Server

**Риск:** `NotificationSenderService` (и другие Server-side hosted services) не используют
distributed lock / leader election. Если запустить две реплики Server, обе будут параллельно
читать `NotificationOutbox` и пытаться отправить уведомления. `LockedUntil` и claim-логика
частично защищают, но не гарантируют single-writer при перекрывающихся окнах.

**Статус:** Принятый риск. Текущая инфраструктура предполагает **один экземпляр Server**.
Multi-instance — planned enhancement, на данный момент не требуется.

**Когда понадобится:**
- 2+ Server за одним ботом (горизонтальное масштабирование)
- Использовать PostgreSQL advisory lock или внешний координатор (etcd / Redis)
-

---

## 3. Отсутствие runtime strict-валидации task-файла по XSD

**Риск:** `CommandPreparer.CreateTaskFile` пишет `TaskFile` XML без runtime-валидации по
`TaskFile.schema.xsd`. Если модель `TaskFile` в коде расходится с XSD-схемой, расхождение
будет обнаружено только в рантайме (плагин не сможет распарсить или прочитает не те поля).

**Статус:** ⚠️ **Остаточный риск** — XSD-схемы обновлены до XML-контракта, но runtime-валидация перед записью
в `TaskDirectory` не реализована. Практическая защита сейчас — синхронное обновление эталона, схем, моделей
и успешная сборка.

---

## 4. `outputFiles` — singular string despite plural name

**Риск:** Поле `ResultFile.OutputFiles` названо во множественном числе, но является `string?`,
а не массивом. Это соответствует канону в `RevitBIMFusion/Docs/ResultFile.schema.xsd`,
но может ввести в заблуждение разработчика, ожидающего список файлов.

**Статус:** Зафиксировано в контракте. Изменение потребует координации с RevitBIMFusion
(плагин, схемы, парсеры).

---

## 5. ~~`Worker:Partitions` — legacy название~~ ✅ Исправлено

**Риск:** ~~Название секции `Worker:Partitions` вводит в заблуждение — ключи словаря больше
не используются для routing, итоговый лимит равен сумме значений. В БД `Commands.Partition`
живёт логическая партиция исходного файла, и это другой концепт.~~

**Статус:** ✅ **Исправлено.** Переименовано в `Worker:MaxConcurrentCommands` и упрощено из `SortedDictionary<int,int>` в `int`.
