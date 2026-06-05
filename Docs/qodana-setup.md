# Настройка Qodana для TelegramBot

[Qodana](https://www.jetbrains.com/qodana/) — это платформа для контроля качества кода от JetBrains, которая переносит проверки из Rider/ReSharper в CI/CD.

## Локальный запуск

### 1. Через Rider (рекомендуется)
1. Откройте проект в Rider.
2. Перейдите в меню **Tools | Qodana | Try Code Analysis with Qodana**.
3. Выберите конфигурацию и нажмите **Run**.

### 2. Через Docker
Если у вас установлен Docker, вы можете запустить анализ командой:
```bash
docker run --rm -v ${PWD}:/data/project/ -p 8080:8080 jetbrains/qodana-dotnet --show-report
```
После завершения отчет будет доступен по адресу `http://localhost:8080`.

## Настройка в CI/CD (GitHub Actions)

Создайте файл `.github/workflows/qodana.yml`:

```yaml
name: Qodana
on:
  workflow_dispatch:
  pull_request:
  push:
    branches:
      - main
      - master

jobs:
  qodana:
    runs-on: ubuntu-latest
    permissions:
      contents: write
      pull-requests: write
      checks: write
    steps:
      - uses: actions/checkout@v4
        with:
          ref: ${{ github.event.pull_request.head.sha }}
          fetch-depth: 0
      - name: 'Qodana Scan'
        uses: JetBrains/qodana-action@v2024.1
        env:
          QODANA_TOKEN: ${{ secrets.QODANA_TOKEN }}
```

## Конфигурация
Файл `qodana.yaml` в корне проекта содержит основные настройки:
- **linter**: используемый образ линтера (`jetbrains/qodana-dotnet`).
- **dotnet**: путь к решению (`TelegramBot.slnx`).
- **profile**: используемый профиль проверок (`qodana.recommended`).

## Особенности проекта
- Тесты в данном проекте отключены согласно `AGENTS.md`. Qodana настроена только на анализ статического кода.
- Используется .NET 10. Убедитесь, что используемая версия линтера поддерживает этот SDK.
