# Worker: стартовый stagger-gate для внешних процессов

## Проблема

`CommandExecutionService.DrainPendingCommandsAsync` запускает claimed-команды как фоновые `Task` без
ожидания, поэтому несколько `Revit.exe` могут стартовать практически одновременно (в пределах партиционного
пула, напр. PDF=3 слота). Встроенный в Revit Chromium-компонент (CEF) пытается забиндить devtools-порт при
старте; при одновременном старте нескольких `Revit.exe` происходит коллизия порта
(`bind() returned an error... WSAEADDRINUSE`), и процесс падает с `ACCESS_VIOLATION (0xC0000005)`.

Подтверждено вручную: запуск Worker против реальной очереди (`B:\103_ENERGY\...`) дал 3 параллельных PDF
(пул=3) → 2 из 3 сразу упали с этим кодом.

## Цель / не-цель

- **Цель:** устранить коллизию старта без потери параллелизма выполнения. Команды должны **стартовать по
  очереди**, но **выполняться параллельно** (как и раньше, в рамках существующих partition-пулов).
- **Не-цель:** не трогаем `PartitionPoolManager` (лимиты параллельной работы) и не вводим per-процесс
  уникальные debug-порты — избыточная сложность для решаемой задачи.

## Решение

Один глобальный stagger-gate в `ProcessRunner`, сериализующий момент `Process.Start()` для **всех** типов
команд (без спец-кейсов по `CommandText` — проще и безопасно, лишняя пауза для не-Revit процессов не
критична).

```csharp
private readonly SemaphoreSlim _launchGate = new(1, 1);

// перед Process.Start():
await _launchGate.WaitAsync(ct);
try
{
    process.Start();
    // держим gate, пока CEF в новом Revit успеет забиндить порт
    await Task.Delay(TimeSpan.FromSeconds(_workerOptions.LaunchStaggerSeconds), CancellationToken.None);
}
finally
{
    _launchGate.Release();
}
```

`CancellationToken.None` в `Task.Delay` — gate всегда отпускается даже при shutdown (`ct` cancelled), иначе
следующий старт в очереди гарантированно зависнет на disposed/cancelled semaphore.

## Конфигурация

`WorkerOptions.LaunchStaggerSeconds` (int, default `5`). Без эмпирики по реальному времени бинда порта в
Revit — дефолт осторожный, значение можно подправить через `appsettings.json` без пересборки.

Валидация (по аналогии с другими полями `WorkerOptions`): `LaunchStaggerSeconds >= 0` (0 = gate выключен,
эквивалент текущему поведению — на случай если после наблюдения окажется не нужен).

## Влияние на существующий алгоритм

- `PartitionPoolManager` (приоритетные пулы) — без изменений. Gate не уменьшает суммарный параллелизм
  выполнения, только разносит во времени **моменты старта**.
- `DrainPendingCommandsAsync` — без изменений в логике claim/drain.
- Побочный эффект: при батче из N команд общее время до старта последней растёт на `~N × StaggerSeconds`.
  Это приемлемо — Revit-задачи сами идут минуты/часы, += несколько секунд старта не значимо.

## Тестирование

Юнит-тест на `ProcessRunner`/gate не оправдан (semaphore + `Process.Start()` — тонкая интеграция с реальным
процессом). Минимальная проверка: ручной прогон Worker против batch из 3+ PDF-команд (как было сделано при
диагностике) — убедиться, что `Process.Start()` вызовы в логе (`External process start`) разнесены по
времени на `LaunchStaggerSeconds`, и что Revit-процессы больше не падают с `ACCESS_VIOLATION` сразу после
старта.
