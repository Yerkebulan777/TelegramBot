-- ============================================================
-- Migration 003: Soft-delete legacy cancelled commands
-- ============================================================
-- Cancellation is represented as command deletion now.
-- This migration converts historical Cancelled rows to Deleted.
-- Safe to run multiple times.
-- ============================================================

UPDATE Commands
SET Status = 'Deleted'
WHERE Status = 'Cancelled';
