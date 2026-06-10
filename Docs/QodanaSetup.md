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

Интеграция Qodana в репозиторий настроена в файле `.github/workflows/code_quality.yml`:

```yaml
name: Qodana Code Quality

# Бесплатная версия: Qodana Community for .NET (qodana-cdnet)
# Основана на ReSharper, Docker-only, C# + VB.NET + C/C++
# QODANA_TOKEN опционален — нужен только для загрузки отчётов в Qodana Cloud

on:
  push:
    branches: [ main, master ]
  pull_request:

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
        ref: ${{ github.event.pull_request.head.sha }}  # анализировать реальный коммит PR, не merge-коммит
        fetch-depth: 0  # полная история нужна для инкрементального анализа PR

    - name: Qodana Scan
      uses: JetBrains/qodana-action@v2026.1
      with:
        pr-mode: false
        args: |
          --linter qodana-cdnet
          --solution TelegramBot.slnx
```

## Конфигурация
Файл `qodana.yaml` в корне проекта содержит основные настройки:
- **linter**: используемый образ линтера (`jetbrains/qodana-dotnet:latest`).
- **dotnet**: путь к решению (`TelegramBot.slnx`).
- **profile**: используемый профиль проверок (`qodana.recommended`).

## Интеграция с CI

Qodana успешно интегрирована в репозиторий как отдельный шаг контроля качества кода (workflow `Qodana Code Quality`).

Текущий CI-пайплайн (`.github/workflows/ci.yml`) также включает:
- `dotnet format --verify-no-changes` — проверка стиля кода
- `dotnet build` — проверка сборки
- `dotnet publish` — публикация артефакта

## Особенности проекта
- Тесты в данном проекте отключены согласно [AGENTS.md](../AGENTS.md). Qodana настроена только на анализ статического кода.
- Используется .NET 10. Убедитесь, что используемая версия линтера поддерживает этот SDK.
