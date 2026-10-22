# IntellectHub

Консольный клиент для разработки с помощью языковых моделей (LLM) — Claude/Claude Code по духу, но с own реализацией на .NET.

## Поддерживаемые провайдеры

- **Anthropic** (Claude) — нативный `/v1/messages`
- **OpenAI-совместимые** (DeepSeek, OpenRouter, и др.) — `/v1/chat/completions`
- **Google Gemini** — через OpenAI-совместимый эндпоинт `https://generativelanguage.googleapis.com/v1beta/openai`

### Google Gemini

Клиент имеет встроенную предустановку `google` (см. `Config.Google()`), которая использует:
- Endpoint: `https://generativelanguage.googleapis.com/v1beta/openai`
- Модель по умолчанию: `gemini-3.8-flash`
- Аутентификация: `x-goog-api-key` + `Authorization: Bearer`

API ключ можно задать:
- через команду клиента: `/connect key google <ваш-ключ>`
- через переменную окружения: `IH_API_KEY=<ключ>`

Реализованы retry с экспоненциальной задержкой (2 с, 4 с, 8 с) для временных ошибок провайдера (HTTP 503, 429, 502, 504), а также парсинг ответов Google в нативном формате (`candidates[].content.parts[].text`) и OpenAI-совместимом формате (`choices[].message.content`).

## Режимы работы

Проект проходит через режимы по порядку: **Авто → Архитектор → Код → (при ошибках) Отладка → Код …**

- **Архитектор** — проектирует архитектуру и пишет план в `.IntellectHub/architecture.md` и `.IntellectHub/plan.md`.
- **Код** — пишет и меняет код проекта через инструменты `write`/`edit`.
- **Отладка** — анализирует ошибки, пишет `.IntellectHub/debug-log.md`, исправляет код и возвращает в режим Код.

## Тестирование

Минимально протестировано на разработке небольшой программы (текстовая игра в стиле Reigns на HTML + JS + CSS, проект `c:\research\games\Reigns`). Подзадачи breakdown, запись файлов через инструменты `write`/`edit` и переход между режимами работают корректно.

## Структура

- `IntellectHub.Client` — консольный клиент (Program.cs, ApiClient.cs, инструменты, барьеры подтверждения)
- `IntellectHub.Core` — ядро: режимы (ModeManager), состояние проекта (ModeStore), артефакты
- `IntellectHub.Tests` — набор тестов (парсинг tool calls, планировщик подзадач, интеграционные)

## Запуск

```bash
dotnet run --project IntellectHub.Client
```

или собранный `.exe` из `IntellectHub.Client/bin/Debug/`.

## Команды в клиенте

- `/connect key <имя> <ключ>` — сохранить API ключ провайдера
- `/mode <имя>` — явно выбрать режим (auto, architect, code, debug)
- `/approve ask|writes|all` — режим подтверждения вызовов инструментов
- `F1` — справка, `Ctrl+K` — команды, `Ctrl+O` — настройки
