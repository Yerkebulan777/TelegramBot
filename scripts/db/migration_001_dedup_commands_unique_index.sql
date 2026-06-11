-- ============================================================
-- Migration 001: Deduplicate Commands + Add UNIQUE INDEX
-- ============================================================
-- Цель: Удалить дубликаты (SessionId, CommandText, FilePath)
--       и добавить UNIQUE INDEX для защиты от дублей в будущем.
-- Безопасно для многократного запуска (идемпотентно).
-- ============================================================

BEGIN;

-- Шаг 1: Удаляем дубликаты, приоритизируя активные команды (pending/processing)
-- Порядок приоритета:
--   1. pending / processing (активная работа)
--   2. Done / Failed / Cancelled (завершённые)
--   3. Deleted (мягко удалённые — наименьший приоритет)
DELETE FROM Commands
WHERE CommandId IN (
    SELECT CommandId
    FROM (
        SELECT CommandId,
               ROW_NUMBER() OVER (
                   PARTITION BY SessionId, CommandText, FilePath
                   ORDER BY
                       CASE WHEN Status IN ('pending', 'processing') THEN 0
                            WHEN Status IN ('Done', 'Failed', 'Cancelled') THEN 1
                            ELSE 2
                       END,
                       CommandId
               ) AS rn
        FROM Commands
    ) dups
    WHERE dups.rn > 1
);

-- Шаг 2: Создаём уникальный индекс (IF NOT EXISTS — безопасен при повторном запуске)
CREATE UNIQUE INDEX IF NOT EXISTS idx_commands_unique
    ON Commands(SessionId, CommandText, FilePath);

COMMIT;
