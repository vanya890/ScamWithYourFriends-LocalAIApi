# LocalAIApiMod

Мод для **Scam With Your Friends**, который перенаправляет ИИ-диалоги игры на
локальный OpenAI-совместимый сервер. Игра перестаёт зависеть от официального
бэкенда (Edge/OpenRouter/Qwen) и генерирует реплики через ваш эндпоинт
(Ollama, LM Studio, LocalAI, vLLM, llama.cpp, magpie и любой другой
OpenAI-совместимый шлюз).

## Как это работает

В этой сборке игры BepInEx **отключён** (`doorstop_config.ini` → `enabled = false`,
`winhttp.dll` отсутствует), поэтому классический Harmony-хук не грузится.
Мод работает иначе — через **офлайн IL-патч** игровых сборок:

| Сборка | Что патчится | Результат |
| --- | --- | --- |
| `Assembly-CSharp.dll` | `KolkataApi::CompleteOpenRouterAsync` | Вызов перенаправляется в `LocalAIApiRuntime.CompleteChatCompletionAsync` |
| `Assembly-CSharp.dll` | `KolkataApi::get_BackendAuthenticated`, `get_BackendAuthStatus` и флаги | Игра считает, что вход выполнен |
| `Assembly-CSharp.dll` | `KolkataApi::StartEdgeAuthentication`, `MaintainEdgeSessionAsync`, `EnsureEdgeSessionAsync` | Сессии официального бэкенда отключены |
| `Assembly-CSharp.dll` | `KolkataApi::Awake` | Инжектится вызов `LocalAIApiRuntime.EnsureConfig` |
| `ScriptsAssDef.dll` | `HostAiSettings` | Форсится режим direct/OpenRouter, `HasInvalidKey = false`, ключ подставляется из патчера |

`LocalAIApiMod.dll` — рантайм мода. Он **не** зависит от BepInEx и линкуется
только с `mscorlib`, `System`, `UniTask` и `Newtonsoft.Json` из состава игры.

> Игровой Mono — старый. В нём нет `File.ReadAllLines`, `WebClient` и
> `HttpWebRequest.GetRequestStream`, поэтому транспорт реализован на «сыром»
> `TcpClient` с HTTP/1.0 и разбором `Content-Length`.

## Требования

- Windows, .NET SDK 9 (для патчера) и .NET Framework 4.0 reference assemblies
  (подтягиваются NuGet-пакетом, отдельная установка VS не нужна).
- Установленная игра Scam With Your Friends.
- Локальный OpenAI-совместимый сервер с `POST /v1/chat/completions`.
- PowerShell 7 (`pwsh`) для скриптов.

## Быстрый старт

```powershell
git clone https://github.com/vanya890/ScamWithYourFriends-LocalAIApi.git
cd ScamWithYourFriends-LocalAIApi
Copy-Item game.dir.example game.dir
notepad game.dir                       # укажите путь к игре
pwsh -File scripts\deploy.ps1          # сборка + патч + установка
```

Дальше отредактируйте конфиг и запустите игру:

```powershell
notepad "$((Get-Content game.dir).Trim())\BepInEx\plugins\LocalAIApi\LocalAIApi.cfg"
```

## Конфигурация

Файл ищется в двух местах (первый найденный побеждает):

1. `<GameDir>\BepInEx\plugins\LocalAIApi\LocalAIApi.cfg`
2. `<GameDir>\LocalAIApi.cfg`

| Параметр | Описание | По умолчанию |
| --- | --- | --- |
| `Enabled` | Включить перенаправление | `true` |
| `ApiEndpoint` | Полный URL chat-completions | `http://127.0.0.1:3425/v1/chat/completions` |
| `ApiKey` | Ключ (уходит в `Authorization: Bearer`) | `magpie` |
| `ModelName` | Модель; **перезаписывает** модель из запроса игры | `group/never-stuck` |
| `TimeoutSeconds` | Таймаут сокета | `60` |
| `Temperature` | Подставляется, если игра не задала | `0.7` |
| `MaxTokens` | Подставляется, если игра не задала | `512` |
| `DebugLogging` | Подробный лог в `LocalAIApi_runtime.log` | `true` |
| `AutoDiscoverAtomicChat` | Автопоиск локального Atomic Chat | `false` |

Примеры:

```ini
# Ollama
ApiEndpoint = http://127.0.0.1:11434/v1/chat/completions
ApiKey =
ModelName = qwen3:4b

# LM Studio
ApiEndpoint = http://127.0.0.1:1234/v1/chat/completions
ModelName = local-model

# magpie (мультипровайдерный шлюз)
ApiEndpoint = http://127.0.0.1:3425/v1/chat/completions
ApiKey = magpie
ModelName = group/never-stuck
```

## Скрипты

| Скрипт | Назначение |
| --- | --- |
| `scripts\build.ps1` | Собирает мод и патчер |
| `scripts\deploy.ps1` | Патчит из чистых бэкапов и разворачивает в `Managed` |
| `scripts\restore.ps1` | Возвращает оригинальные сборки игры |
| `scripts\smoke-test.ps1` | Проверяет эндпоинт и состояние развёртывания |

Патчер можно вызвать напрямую:

```
LocalAIApiPatcher --managed <ManagedDir> --output <OutDir> --mod <LocalAIApiMod.dll> `
                 --api-key <key> [--assembly-csharp <clean.dll>] [--scripts-assdef <clean.dll>]
```

## Диагностика

Проверить, что запросы действительно уходят на локальный сервер:

```powershell
Get-Content "$((Get-Content game.dir).Trim())\LocalAIApi_runtime.log" -Tail 20
```

Ожидаемые строки — `SendLocalChatRequest: sending request to …`,
`HTTP response in Nms`, `choice message: …`.

| Симптом | Причина / решение |
| --- | --- |
| «Сервис ИИ временно недоступен» | Эндпоинт не отвечает, истёк таймаут или запрос отменён. Проверьте `LocalAIApi_runtime.log` |
| «Вход в ИИ недоступен. Перезапустите игру через Steam» | Патч аутентификации не применился — повторите `scripts\deploy.ps1` |
| В логе `header delimiter not found` | Сервер отвечает chunked-кодированием. Нужен сервер с `Content-Length` |
| Игра не видит мод | В `Managed` не скопирован `LocalAIApiMod.dll` либо правлен не тот `Assembly-CSharp.dll` |
| Ответы пустые | Включите `DebugLogging`; рантайм переносит `reasoning_content` в `content`, если модель вернула пустой `content` |

## Откат

```powershell
pwsh -File scripts\restore.ps1
```

Оригиналы хранятся в `<GameDir>\_LocalAIApiMod_backup`. Если каталог утерян,
восстановите целостность файлов игры средствами Steam.

## Структура репозитория

```
Directory.Build.props              резолвинг GameDir / ManagedDir
game.dir.example                   шаблон пути к игре
config\LocalAIApi.cfg.example      шаблон конфигурации
src\LocalAIApiMod\                 рантайм мода (net40)
src\LocalAIApiPatcher\             IL-патчер (net9.0 + Mono.Cecil)
scripts\                           build / deploy / restore / smoke-test
docs\ARCHITECTURE.md               детали IL-патча и рантайма
```

Подробности реализации — в [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Правовая оговорка

Репозиторий не содержит игровых файлов, ресурсов или ассемблей. Патч
применяется локально к вашей лицензионной копии. Используйте на свой риск и
соблюдайте правила игры.
