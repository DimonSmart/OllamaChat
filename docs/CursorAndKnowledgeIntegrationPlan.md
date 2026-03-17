# План внедрения cursor-driven обработки, file sandbox MCP и knowledge MCP

## Зачем это нужно

Текущий проект уже умеет:

- подключать built-in и external MCP серверы через общий каталог инструментов;
- использовать один и тот же каталог инструментов и в обычном agentic chat, и в planning runtime;
- ограничивать инструменты агента через `AgentDescription.FunctionSettings`;
- строить multi-step планы с fan-out через `mode=map`.

Этого достаточно для небольших задач, но недостаточно для задач следующего класса:

- обзор 100, 500 или 1000 Источников;
- поиск по большим папкам с файлами;
- извлечение сущностей из больших документов или коллекций документов;
- сценарии вида "читай источник порциями, копи evidence, периодически своди промежуточный результат".

Главная проблема не в отсутствии LLM, а в отсутствии архитектуры для данных, которые заведомо больше контекстного окна модели.

Идея из `AITextEditor`, которую имеет смысл перенести в текущий проект:

- курсоры как именованные ограниченные потоки данных;
- чтение батчами с resume token / pointer;
- подагент для потокового анализа батчей;
- отдельный knowledge state, который обновляется по мере прохода;
- финализация поверх накопленного evidence, а не поверх полного сырых данных.

## Что именно делаем

Мы не пытаемся сразу переписать planning runtime под произвольные циклы.

На первом этапе мы добавляем cursor-driven возможности в виде MCP инструментов, чтобы:

- обычный agentic chat уже мог работать с большими источниками;
- planning runtime получил эти же возможности автоматически через общий `AppToolCatalog`;
- новые возможности можно было внедрять постепенно без поломки текущего `PlanDefinition`.

Целевая архитектурная цель:

- большие данные живут не в `PlanStep.Result`, а во внешнем state/artifact store;
- LLM получает только очередной батч, сжатый snapshot состояния и итоговый evidence;
- session-scoped параметры MCP, например `roots` для file-sandbox MCP и `knowledgeFile` для knowledge MCP, могут задаваться при запуске конкретной команды/сессии без изменения сохраненной конфигурации сервера;
- поверх cursor/runtime можно строить доменные MCP серверы, например `character knowledge`, `entity dossiers`, `package review`, `document index`.

## Что уже есть в текущем проекте

### Общая tool-платформа уже подходит

Сейчас проект уже имеет правильную базу для расширения:

- built-in MCP серверы регистрируются через `BuiltInMcpServerCatalog` и `BuiltInMcpServerHost`;
- все MCP инструменты собираются в общий `AppToolCatalog`;
- planning runtime использует тот же общий каталог инструментов;
- agentic runtime тоже резолвит инструменты из того же каталога.

Это означает:

- новый built-in MCP сервер сразу станет доступен и в planning runtime, и в agentic chat;
- не требуется отдельный "planner-only tool layer";
- не требуется отдельная схема описания инструментов для агентов.

### Текущая схема агентов уже позволяет завести knowledge MCP

Схема `AgentDescription + FunctionSettings` уже достаточна, чтобы подключить специализированный MCP сервер:

- агент описывается prompt'ом в `AgentDescription`;
- доступные инструменты задаются через `FunctionSettings`;
- runtime умеет ограничить агенту набор инструментов по qualified name;
- инструменты MCP автоматически становятся provider tools для модели.

Следствие:

- отдельный агент `Character Analyst` или `Book Dossier Builder` можно завести уже сейчас;
- если добавить MCP сервер `character-knowledge`, агент сможет использовать только его инструменты и нужные cursor/file инструменты;
- для базового сценария не нужно менять модель `AgentDescription`.

### Что пока не хватает

В текущей реализации есть архитектурные ограничения:

- у локального MCP сервера конфигурация фактически статическая: `Command + Arguments`;
- `McpClientService` кэширует клиентов глобально по fingerprint конфигурации;
- session-scoped launch parameters сейчас отсутствуют;
- `PlanStep.Result` и UI planning state держат результаты inline в памяти;
- built-in web tools имеют жесткие small-N лимиты и обрезают содержимое страниц;
- planning runtime умеет `map/collect/flatten`, но не умеет долгоживущий курсор, checkpoint и иерархический reduce.

## Что берем из AITextEditor

Из `AITextEditor` имеет смысл переносить не весь продукт, а следующие принципы:

1. Cursor как именованный поток элементов, читаемый порциями.
2. Порция ограничивается и по количеству элементов, и по байтовому бюджету.
3. Есть pointer / token для продолжения чтения.
4. Есть подагент, который на каждом батче возвращает очень маленький JSON-командный ответ.
5. Есть evidence snapshot, который не разрастается до размера исходного корпуса.
6. Есть финализатор, выбирающий итог по evidence, а не по полному документу.
7. Доменные знания живут в отдельном state store и обновляются инкрементально.

## Что не надо переносить как есть

Некоторые вещи из `AITextEditor` не нужно переносить напрямую:

- временные лимиты вроде `TemporaryTotalItemLimit`;
- state, который живет только в памяти процесса MCP;
- узко-книжную модель как единственную форму данных;
- доменный слой "персонажи книги" как слишком специальный API.

Нам нужен более общий слой:

- file items;
- document chunks;
- search hits;
- extracted entities;
- evidence records;
- knowledge artifacts.

## Целевая архитектура

### 1. Session-scoped MCP binding

Нужен новый слой между сохраненной конфигурацией MCP сервера и конкретным runtime запуском.

Идея:

- `McpServerConfig` хранит только постоянную конфигурацию сервера;
- при запуске chat/planning run можно передать `session parameters` для конкретного сервера;
- локальный MCP процесс стартует с этими параметрами только для текущей сессии;
- сохраненная конфигурация сервера при этом не меняется.

Это особенно важно для file MCP:

- один и тот же сервер может быть определен как `sandboxed-files`;
- но для одного запуска ему задается root `C:\RepoA`;
- для другого запуска root `D:\Docs`;
- вне этих roots сервер не должен иметь доступа к файлам.

Для knowledge MCP, который работает поверх одного заранее заданного файла, `roots` не обязательны:

- достаточно передать `knowledgeFile` при старте конкретного run;
- сам MCP не должен уметь переключаться на другой файл через runtime-команды;
- ограничения вида `roots` нужны уже для отдельного file-sandbox MCP, где сервер реально ходит по файловому дереву.

### 2. Cursor runtime как shared application service

Нужен общий cursor слой внутри приложения:

- создание курсора из источника;
- чтение очередного батча;
- хранение progress/checkpoint;
- завершение и очистка курсора;
- persisted state для длинных операций.

Этот слой не должен зависеть от конкретного домена "книга".

### 3. Artifact/state store для больших результатов

Нужен persistent store для long-running state:

- cursor state;
- evidence;
- промежуточные summaries;
- extracted entities;
- финальные artifacts.

Важный принцип:

- в `PlanStep.Result` хранится только preview, counters и `artifactRef`;
- большие массивы и сырые данные хранятся вне plan state.

### 4. Cursor Agent MCP

Нужен built-in MCP сервер, который умеет:

- брать существующий cursor;
- читать его батчами;
- выполнять bounded loop;
- накапливать evidence;
- выдавать итог + token для продолжения.

Это даст системе универсальный шаблон long-running анализа, не завязанный на один домен.

### 5. Domain knowledge MCP

Поверх cursor runtime можно строить специализированные knowledge MCP серверы:

- `character-knowledge`;
- `entity-dossiers`;
- `package-review-knowledge`;
- `document-facts`.

Правильный путь:

- сначала сделать generic entity knowledge слой;
- затем на его основе сделать specialization для персонажей книги как maturity test.

## Какие MCP серверы стоит добавить

### Минимально необходимые

1. `built-in-file-sandbox`
2. `built-in-cursor`
3. `built-in-cursor-agent`
4. `built-in-artifact-store`

### Доменные после фундамента

5. `built-in-entity-knowledge`
6. `built-in-character-knowledge`
7. `built-in-document-linear` или `built-in-document-chunks`

## Что должен уметь каждый сервер

### `built-in-file-sandbox`

Назначение:

- безопасный доступ к файлам только внутри явно заданных roots.

Базовые инструменты:

- `list_roots()`
- `list_files(path?, glob?, recurse?)`
- `read_file(path, start?, length?)`
- `grep_files(query, glob?, recurse?, caseSensitive?, regex?)`
- `stat(path)`

Требования безопасности:

- каждый path нормализуется до absolute canonical path;
- доступ разрешен только если path лежит внутри одного из allowed roots;
- path traversal, symlink escape и UNC edge cases должны быть закрыты;
- сервер должен возвращать структурированную ошибку `outside_allowed_roots`.

### `built-in-cursor`

Назначение:

- создавать именованные курсоры поверх файлов, чанков документов, search results и других коллекций.

Инструменты:

- `create_file_cursor(root, glob?, recurse?, includeMetadata?)`
- `create_search_result_cursor(items, batchSize?, byteBudget?)`
- `create_chunk_cursor(artifactRef, batchSize?, byteBudget?)`
- `read_cursor_batch(cursorId, nextToken?)`
- `describe_cursor(cursorId)`
- `dispose_cursor(cursorId)`

Возвращаемые данные:

- `cursorId`
- `nextToken`
- `hasMore`
- `items`
- `stats`

### `built-in-cursor-agent`

Назначение:

- управляемый bounded loop поверх cursor batches.

Инструменты:

- `run_cursor_agent(cursorId, taskDescription, startAfterToken?, maxEvidenceCount?, maxSteps?)`
- `resume_cursor_agent(runId)`
- `get_cursor_agent_result(runId)`

Результат:

- `runId`
- `status`
- `summary`
- `evidenceRef`
- `nextToken`
- `cursorComplete`

### `built-in-artifact-store`

Назначение:

- хранить большие промежуточные результаты отдельно от step state.

Инструменты:

- `save_artifact(kind, data, metadata?)`
- `read_artifact(artifactRef, projection?)`
- `append_artifact(artifactRef, items)`
- `summarize_artifact(artifactRef)`

### `built-in-entity-knowledge`

Назначение:

- generic knowledge/catalog store для сущностей, extracted facts и связей.

Инструменты:

- `create_schema(schemaName, fields, matchingRules?)`
- `upsert_entities(schemaName, entities)`
- `get_entities(schemaName, filter?)`
- `refresh_entities_from_artifact(schemaName, artifactRef)`
- `export_entities(schemaName, format?)`

### `built-in-character-knowledge`

Назначение:

- specialization для кейса "собери досье на персонажей".

Инструменты:

- `generate_character_dossiers(sourceRef?)`
- `update_character_dossiers_from_cursor(cursorId)`
- `get_character_dossiers()`
- `refresh_character_dossiers(changedPointers?)`
- `upsert_character_dossier(...)`

Этот сервер нужен не как конечная цель, а как проверка зрелости платформы.

## Как реализовать roots без пересоздания server config

### Текущее ограничение

Сейчас roots нельзя задавать per-run без изменения saved config, потому что:

- `McpServerConfig` хранит только статические `Command`, `Arguments`, `Sse`;
- `McpClientService` кэширует клиентов глобально по fingerprint конфигурации;
- session-specific transport parameters отсутствуют.

### Что нужно добавить

Нужна новая сущность уровня runtime:

- `McpSessionBinding`

Пример формы:

```json
{
  "serverId": "guid-or-name",
  "launchOverrides": {
    "roots": [
      "C:\\Work\\RepoA",
      "C:\\Docs\\Book1"
    ]
  }
}
```

И новая модель runtime request:

- для agentic chat;
- для planning session;
- для MCP playground, если нужно вручную тестировать.

### Как это должно работать

1. Пользователь выбирает MCP сервер `sandboxed-files`.
2. При запуске конкретной команды UI передает session binding с `roots`.
3. Приложение поднимает session-scoped MCP client только для этой сессии.
4. В fingerprint session client входят `server config + launch overrides`.
5. Saved config сервера не меняется.
6. После завершения run session client освобождается.

### Что это меняет в коде

Нужно расширить:

- `AgenticExecutionRuntimeRequest`
- `PlanningRunRequest`
- `McpClientService`

Нужно разделить клиентов на два класса:

- global cached clients;
- session-scoped clients.

Практический вывод:

- "без пересоздания конфигурации сервера" достижимо;
- "без создания нового процесса/клиента вообще" для stdio MCP не обязательно достижимо и не является целью;
- важна именно неизменность saved server config и безопасная session isolation.

## Интеграция идеи книжного редактирования из AITextEditor

Это можно сделать поэтапно.

### Этап A. Перенести принципы, не домен

Сначала переносим:

- курсоры;
- pointer/token continuation;
- bounded batch processing;
- evidence snapshot;
- finalizer;
- artifact store.

### Этап B. Перенести document-linear инструменты

После этого можно добавить документно-ориентированные MCP серверы:

- загрузка markdown/document chunks;
- список semantic pointers;
- создание target set;
- применение bounded edit operations.

### Этап C. Перенести книжные сценарии

Когда foundation готов, можно добавить:

- character knowledge;
- chapter navigation;
- paragraph-level find/transform;
- dossier generation.

Именно так появляется "идея книжного редактирования", но без жесткой привязки текущего проекта к одному домену.

## Проверка зрелости системы: character knowledge как maturity test

### Может ли текущая схема это поддержать

Для базовой интеграции ответ: да.

Почему:

- новый MCP сервер автоматически попадает в общий каталог инструментов;
- agentic runtime умеет ограничивать инструменты по `FunctionSettings`;
- planning runtime использует тот же каталог;
- специализированный агент можно описать обычным `AgentDescription`.

### Где текущая схема еще слабая

Для production-grade knowledge server пока не хватает:

- session-scoped MCP parameters;
- long-run persisted state;
- artifact references вместо больших inline JSON результатов;
- UI для run progress и resume;
- договоренности по storage lifecycle.

### Почему это хороший тест

Если система сможет устойчиво поддержать сценарий:

- cursor -> extract -> update knowledge -> query knowledge -> synthesize answer,

то это значит, что платформа уже годится не только для "вызови пару tools", а для настоящих stateful agent workflows.

## План работ

### Фаза 0. Архитектурная подготовка

Цель:

- ввести термины и контракты без большого рефакторинга planner.

Шаги:

1. Добавить архитектурный ADR/plan по cursor-driven обработке.
2. Зафиксировать правило: большие результаты не кладем inline, только preview + `artifactRef`.
3. Определить общие DTO:
   - `CursorDescriptor`
   - `CursorBatch`
   - `ArtifactRef`
   - `EvidenceRecord`
   - `McpSessionBinding`
4. Зафиксировать error contract для sandbox и cursor operations.

### Фаза 1. Session-scoped MCP launch overrides

Цель:

- уметь запускать локальный MCP сервер с параметрами `roots` без изменения saved config.

Шаги:

1. Расширить runtime requests session bindings.
2. Добавить в `McpClientService` session-scoped client creation.
3. Ввести fingerprint по `config + overrides`.
4. Добавить lifecycle cleanup session clients.
5. Вывести это в UI для planning/agentic запуска.

Результат:

- можно стартовать file MCP с разными roots в разных runs.

### Фаза 2. File sandbox MCP

Цель:

- безопасный доступ к файловому дереву внутри разрешенных roots.

Шаги:

1. Реализовать built-in `file-sandbox` сервер.
2. Добавить canonical path validation.
3. Закрыть path traversal и symlink escape.
4. Добавить инструменты `list/read/grep/stat`.
5. Добавить тесты на sandbox boundaries.

Результат:

- система может анализировать реальные файлы, не выходя за root.

### Фаза 3. Shared cursor runtime

Цель:

- получить универсальный слой long-running batch processing.

Шаги:

1. Добавить `ICursorStore`/`ICursorRuntime` в приложение.
2. Реализовать persistent cursor progress store.
3. Реализовать file cursor и chunk cursor.
4. Добавить `read_cursor_batch`.
5. Добавить resume token / next token.

Результат:

- источник читается порциями независимо от planning runtime.

### Фаза 4. Artifact store

Цель:

- вынести большие результаты из plan state и chat state.

Шаги:

1. Реализовать `IPlanningArtifactStore`.
2. Добавить сохранение больших коллекций и evidence.
3. Изменить UI/state так, чтобы step details показывали preview и ссылались на artifact.
4. Подготовить DTO `artifactRef`.

Результат:

- long-running сценарии перестают раздувать память и UI state.

### Фаза 5. Cursor Agent MCP

Цель:

- внедрить bounded loop как инструмент, а не как новый step type.

Шаги:

1. Добавить built-in `cursor-agent` MCP.
2. Реализовать цикл:
   - read batch
   - send batch + snapshot to LLM
   - append evidence
   - stop/continue
   - finalize
3. Добавить persisted run record.
4. Добавить resume и progress API.

Результат:

- LLM может анализировать большие корпуса, не видя их целиком.

### Фаза 6. Generic entity knowledge MCP

Цель:

- получить общий knowledge layer для сущностей.

Шаги:

1. Спроектировать generic schema для entity dossier.
2. Реализовать upsert/find/export API.
3. Реализовать refresh по artifact/evidence.
4. Добавить matching/merge rules.

Результат:

- можно строить доменные knowledge servers без дублирования foundation.

### Фаза 7. Character knowledge maturity test

Цель:

- воспроизвести сценарий AITextEditor на текущей платформе.

Шаги:

1. Добавить `character-knowledge` MCP как specialization над entity knowledge.
2. Завести dedicated agent description.
3. Ограничить агенту инструменты:
   - file sandbox
   - cursor
   - cursor-agent
   - character-knowledge
4. Сделать e2e tests:
   - загрузка книги/папки с главами
   - построение досье
   - инкрементальное обновление
   - ответ по knowledge state

Результат:

- система проходит зрелый stateful MCP + agents сценарий.

### Фаза 8. Глубокая интеграция в planning runtime

Цель:

- научить planner нативно использовать cursor patterns.

Шаги:

1. Добавить few-shot examples для cursor/file/entity workflows.
2. Добавить в planner/replanner guidance паттерны:
   - create cursor
   - run cursor agent
   - read artifact
   - synthesize from artifact
3. При необходимости позже добавить first-class step kinds:
   - `cursor`
   - `reduce`
   - `artifact`.

Результат:

- planner начнет строить long-running workflows осознанно.

## Что делать сначала

Рекомендуемый порядок внедрения:

1. Session-scoped launch overrides
2. File sandbox MCP
3. Shared cursor runtime
4. Artifact store
5. Cursor Agent MCP
6. Generic entity knowledge MCP
7. Character knowledge maturity test
8. Planner-native cursor patterns

Это самый прагматичный путь, потому что:

- дает пользу уже после первых двух фаз;
- не требует сразу ломать plan contract;
- позволяет проверять архитектуру на реальных сценариях;
- оставляет книжное редактирование как естественное расширение, а не как hard-coded feature.

## Критерии успеха

Система считается успешно расширенной, если она умеет:

1. Запустить sandboxed file MCP с roots, заданными только для текущего run.
2. Прочитать папку с сотнями файлов без выхода за пределы root.
3. Создать cursor и пройти данные батчами с resume.
4. Держать большие intermediate results вне `PlanStep.Result`.
5. Построить knowledge artifact поверх cursor-driven evidence.
6. Поддержать отдельного knowledge-агента через текущую схему `AgentDescription + FunctionSettings`.
7. Пройти e2e сценарий наподобие character dossier extraction.

## Практический вывод

Текущий проект уже достаточно зрелый, чтобы принять:

- built-in cursor MCP;
- session-scoped sandboxed file MCP;
- generic entity/character knowledge MCP.

Но для этого нужно сознательно сделать еще один архитектурный шаг:

- отделить saved server config от session launch parameters;
- отделить plan/chat state от больших данных;
- сделать cursor и artifact store first-class application concepts.

Именно это создаст основу и для обработки 1000 пакетов, и для анализа файловых папок, и для книжного редактирования в стиле `AITextEditor`.
