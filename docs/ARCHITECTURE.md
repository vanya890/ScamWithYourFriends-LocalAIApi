# Архитектура LocalAIApiMod

Документ описывает, как именно мод встраивается в игру и почему выбран такой
подход. Актуален для сборки `Scam With Your Friends` от 20.10.2026
(`Assembly-CSharp.dll` 2 968 576 байт, `ScriptsAssDef.dll` 3 270 656 байт).

## 1. Почему IL-патч, а не BepInEx

Игра поставляется с Unity Doorstop, но в `doorstop_config.ini` стоит
`enabled = false`, а `winhttp.dll` в корне отсутствует. Значит, BepInEx-плагины
не загружаются вообще. Поэтому мод разделён на две части:

- **офлайн-патчер** (`LocalAIApiPatcher`, net9.0 + Mono.Cecil) — правит
  игровые ассембли на диске;
- **рантайм** (`LocalAIApiMod.dll`, net40) — код, который вызывает игра.

Патчер не хардкодит пути: каталог игры передаётся аргументом `--managed`.

## 2. Точка входа ИИ-запросов

`KolkataApi` отвечает за взаимодействие с бэкендом. Ключевой метод —
`CompleteOpenRouterAsync(JObject body, CancellationToken ct)`. Патчер
очищает его тело и подставляет вызов рантайма:

```il
ldarg.1                       // JObject body
ldarg.2                       // CancellationToken
call LocalAIApiRuntime::CompleteChatCompletionAsync(JObject, CancellationToken)
ret
```

Сигнатура рантайма повторяет оригинальную, что позволяет переиспользовать
существующую машину состояний без переписывания вызывающего кода.

## 3. Обход аутентификации

Игра не запускает игровой процесс, пока не «войдёт» на официальный бэкенд.
Патчер форсит свойства `KolkataApi`:

- `get_BackendAuthStatus` → `2` (SignedIn);
- `get_BackendAuthenticated`, `get_UsesDevelopmentBackend`, `get_CanStartGame` → `true`;
- `get_BrowserSignInAvailable`, `get_BrowserSignInPending`, `get_ShowSignInSupport`,
  `get_SystemClockIncorrect`, `get_AiCreditsDepleted`, `get_AiUsageRestricted` → `false`.

Сетевые сессии Edge нейтрализуются:

- `StartEdgeAuthentication` → пустое тело (`ret`);
- `MaintainEdgeSessionAsync` → `default(UniTaskVoid)`;
- `EnsureEdgeSessionAsync` → `default(UniTask)`.

Для структурных типов возврата генерируется `initobj` по локальной переменной —
это корректный `default(T)` без обращения к несуществующим конструкторам.

## 4. Конфигурация `HostAiSettings`

`ScriptsAssDef.dll` содержит `HostAiSettings`, который решает, как хост
обращается к ИИ. Патч:

| Метод | Значение |
| --- | --- |
| `get_OpenRouterSelected` | `true` |
| `get_DirectSelected` | `true` |
| `get_UseDirectOpenRouter` | `true` |
| `get_HasInvalidKey` | `false` |
| `get_ApiKey` | строка из `--api-key` (по умолчанию `magpie`) |

Значение `get_ApiKey` прошито в IL как `ldstr`, поэтому смена ключа требует
повторного прогона патчера (`scripts\deploy.ps1 -ApiKey <value>`).

## 5. Загрузка конфига рантайма

В `KolkataApi::Awake` патчер вставляет первой инструкцией `call
LocalAIApiRuntime::EnsureConfig()`. Это гарантирует, что `LocalAIApi.cfg`
прочитан до первого ИИ-запроса.

`EnsureConfig` идемпотентен (флаг `_configLoaded`) и ищет файл в порядке:

1. `<GameDir>\BepInEx\plugins\LocalAIApi\LocalAIApi.cfg`
2. `<GameDir>\LocalAIApi.cfg`

`<GameDir>` вычисляется от расположения самой `LocalAIApiMod.dll`
(`Managed` → `*_Data` → корень игры), а не от текущего каталога процесса.

## 6. Транспорт

Игровой Mono не поддерживает `WebClient`, `File.ReadAllLines`,
`StreamReader(string, Encoding)` и `HttpWebRequest.GetRequestStream`, поэтому
рантайм использует:

- чтение файла: `File.ReadAllBytes` + `Encoding.UTF8.GetString`;
- запись лога: `FileStream(FileMode.Append)`;
- HTTP: `TcpClient` + рукописный запрос `HTTP/1.0` с `Content-Length`
  и `Connection: close`, ответ читается целиком и делится по `\r\n\r\n`.

`HTTP/1.0` выбран намеренно: сервер обязан вернуть `Content-Length` и закрыть
соединение, что избавляет от разбора `Transfer-Encoding: chunked`.

Перед отправкой рантайм:

- перезаписывает `model` значением `ModelName`;
- удаляет игровые метаданные (`lobby_session_id`, `ai_category`,
  `prompt_sources`, `conversation_diagnostics` и др.), которые могут смутить
  локальную модель;
- после ответа переносит `choices[0].message.reasoning_content` в `content`,
  если модель вернула пустой `content` (типично для reasoning-моделей).

## 7. Идемпотентность развёртывания

`scripts\deploy.ps1` при первом запуске копирует чистые ассембли в
`<GameDir>\_LocalAIApiMod_backup` и всегда патчит **из них**, а не из текущего
`Managed`. Так повторные запуски не накапливают изменения, а `restore.ps1`
возвращает исходное состояние.

## 8. Проверенные факты

- `Assembly-CSharp.dll.bak` в `Managed` побайтово совпадает с чистым
  оригиналом (`SHA-256 3AAB14D5…87A787B`) — используется как источник бэкапа.
- Эндпоинт `http://127.0.0.1:3425/v1/models` отдаёт `200` и модель
  `group/never-stuck`.
- `LocalAIApi_runtime.log` содержит успешные обмены: `HTTP response in Nms`
  и непустые `choice message`.
