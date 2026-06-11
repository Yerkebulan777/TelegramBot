#!/bin/bash
# Hook: PreToolUse (Edit|Write)
# Блокирует редактирование критичных файлов без явного подтверждения.
# Exit 2 = заблокировать, Exit 0 = разрешить.

INPUT=$(cat)
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // empty')

if [ -z "$FILE_PATH" ]; then
  exit 0
fi

FILENAME=$(basename "$FILE_PATH")

PROTECTED_FILES=("appsettings.json" "appsettings.Development.json" ".credentials.json" "botdata.db")

for protected in "${PROTECTED_FILES[@]}"; do
  if [ "$FILENAME" = "$protected" ]; then
    echo "BLOCKED: '$FILENAME' is a protected file. It contains secrets or database data." >&2
    exit 2
  fi
done

exit 0
