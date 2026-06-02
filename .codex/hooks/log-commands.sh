#!/bin/bash
# Hook: PostToolUse (Bash)
# Логирует все выполненные bash-команды в файл с временной меткой.

INPUT=$(cat)
COMMAND=$(echo "$INPUT" | jq -r '.tool_input.command // empty')

if [ -n "$COMMAND" ]; then
  TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')
  echo "[$TIMESTAMP] $COMMAND" >> "$CLAUDE_PROJECT_DIR/.claude/command-log.txt"
fi

exit 0
