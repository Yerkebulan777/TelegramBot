# TODO / Бэклог

## 🟢 НИЗКО

### 1. Очистка папок экспорта

Логика `ExportFolderCleanupService` и `ExportFolderCleanupOptions` готовы. Нужно:
- [ ] Подключить в DI Worker с расписанием
- [ ] Интегрировать фильтр исключения папок `#*`

Политика: группировка по имени → non-old (≤100d): newest → архив, остальные → удалить. Old: ≥2 дублей → оставить 3, <30MB удалить, ≥30MB в архив. Посторонние файлы → удалить.

### 2. Stagger для Process.Start()

Удалён. При CEF collision — вернуть `await` внутри `_launchGate`.
