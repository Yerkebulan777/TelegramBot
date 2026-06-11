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

### qodana.yaml
Файл `qodana.yaml` в корне проекта содержит основные настройки:
- **linter**: используемый образ линтера (`jetbrains/qodana-dotnet:latest`).
- **dotnet**: путь к решению (`TelegramBot.slnx`).
- **profile**: используемый профиль проверок (`qodana.recommended`).

### .editorconfig — IDisposable инспекции (ERROR level)

Для предотвращения утечек ресурсов в `.editorconfig` явно включены на уровне ERROR три инспекции ReSharper:

```editorconfig
resharper_not_disposed_resource_highlighting = error
resharper_dispose_on_non_disposable_type_highlighting = error
resharper_use_await_using_highlighting = error
```

| Инспекция | Что проверяет | Пример обнаруженного бага |
|-----------|---------------|--------------------------|
| `NotDisposedResource` | Локальная переменная `IDisposable` не диспозится перед выходом из скоупа | `Process` без `Dispose()` в `ProcessRunner` (утечка OS-дескрипторов) |
| `DisposeOnNonDisposableType` | Класс содержит поля `IDisposable`, но не реализует `IDisposable` сам | — |
| `UseAwaitUsing` | В асинхронном методе используется `using` вместо `await using` | — |

Эти правила работают как локально (Rider/ReSharper видит `.editorconfig`), так и в CI (Qodana подхватывает те же настройки).

### Качество кода (Quality Gates)

В CI настроен Quality Gate `any: 0` — любой issue блокирует сборку. Это гарантирует, что новая утечка ресурсов будет обнаружена на этапе PR.

## Интеграция с CI

Qodana успешно интегрирована в репозиторий как отдельный шаг контроля качества кода (workflow `Qodana Code Quality`). Запускается на push в `main`/`master` и каждый PR.

Текущий CI-пайплайн (`.github/workflows/ci.yml`) также включает:
- **`validate-issue-refs`** — проверка, что `fix #N` в commit messages ссылается на существующий Issue (через `gh issue view`). Запускается **до** сборки (`needs: [validate-issue-refs]`). Если Issue не существует — CI фейлится.
- `dotnet format --verify-no-changes` — проверка стиля кода и `.editorconfig`
- `dotnet build` — проверка сборки
- `dotnet publish` — публикация артефакта

> **Дополнение:** `validate-issue-refs` не относится к Qodana, но является частью общего Quality Gate — гарантирует, что каждый закрытый Issue задокументирован в коммите до того, как код попадёт в `master`.

## Особенности проекта
- Тесты в данном проекте отключены согласно [AGENTS.md](../AGENTS.md). Qodana настроена только на анализ статического кода.
- Используется .NET 10. Убедитесь, что используемая версия линтера поддерживает этот SDK.

## Что делать, если Qodana не видит .editorconfig

1. Убедитесь, что `qodana.yaml` содержит `profile: name: qodana.recommended` (он включает все ReSharper инспекции).
2. Проверьте, что `*.editorconfig*` не исключён в секции `exclude` `qodana.yaml`.
3. Для верификации локально: `dotnet format --verify-no-changes` проверяет только `.editorconfig`-стиль, но не ReSharper/Qodana инспекции. Для полной проверки используйте `docker run` с Qodana (см. выше).
4. Если инспекция всё ещё не срабатывает — явно задайте severity в `.editorconfig`: `resharper_<id>_highlighting = error`. Это переопределяет и Qodana, и Rider.
