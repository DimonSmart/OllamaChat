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

### Уже реализовано

Сейчас в проекте уже есть важная часть фундамента, которую старый план считал будущей:

- built-in MCP серверы регистрируются через `BuiltInMcpServerCatalog` и запускаются через `BuiltInMcpServerHost`;
- все MCP инструменты собираются в общий `AppToolCatalog`;
- planning runtime и agentic runtime используют один и тот же каталог инструментов;
- в коде уже есть session-scoped binding модель: `McpServerSessionBinding`, `McpClientRequestContext`, `McpSessionBindingTransport`;
- `IMcpServerDescriptor` уже несет `OverrideDefinitions`, а built-in и external MCP серверы уже могут объявлять overridable-параметры рядом со своей регистрацией/конфигурацией;
- `McpClientService` уже строит fingerprint по `server config + request context` и поднимает отдельные client sets для разных binding-контекстов;
- `AgentDescription` и `AppChatConfiguration` уже умеют хранить `McpServerBindings`;
- binding-модель уже поддерживает несколько attachment одного и того же сервера через `BindingId`, а `AppToolCatalog` уже выдает binding-scoped qualified names вида `binding:{id}:{tool}`;
- в UI уже есть first-class editor для agent-level MCP attachments (`McpServerBindingsEditor`) и editor для override definitions в реестре MCP серверов;
- agentic runtime уже умеет мерджить agent-level bindings и run-level bindings;
- built-in `built-in-knowledge-book` уже существует как рабочий MCP сервер с отдельным `knowledgeFile`, CRUD/search/export tools и автотестами.

### Что это меняет для дальнейшего плана

Это означает:

- фаза про shared tool catalog уже завершена и не требует отдельной архитектурной ветки;
- фаза про session-scoped launch overrides больше не greenfield: транспорт и кэширование уже сделаны, осталось довести их до planning runtime и UI;
- фаза про override definitions и instance-based attachments уже в значительной степени сделана, поэтому не нужно проектировать новую сущность поверх `McpServerSessionBinding`;
- knowledge MCP тоже уже не гипотеза: в проекте есть готовая knowledge book, которую можно привязывать к агенту или запуску;
- новые cursor/file/domain MCP серверы надо строить поверх существующего built-in host и общего каталога, а не через отдельный planner-only или agent-only слой.

### Что пока не хватает

Ключевые пробелы на текущий момент:

- `PlanningRunRequest` уже принимает `McpServerBindings`, а `PlanningSessionService` строит binding-specific tool catalog через `McpClientRequestContext`;
- в UI уже есть переиспользуемый `McpServerBindingsEditor` для agent-level, single-agent chat, multi-agent chat и planning run-level конфигурации;
- built-in `file-sandbox` пока не реализован, хотя модель bindings и `roots` уже подготовлены;
- `PlanStep.Result` и `PlanningSessionState` все еще держат результаты inline, без `preview + artifactRef`;
- shared cursor runtime, built-in `cursor` MCP, `cursor-agent` и persisted progress/resume пока отсутствуют;
- planning runtime пока не умеет вызывать заранее настроенных агентов (`AgentDescription`) и делегировать им подзадачи; LLM step там сейчас только ad-hoc prompt без agent registry;
- текущий knowledge book полезен как иерархическое хранилище разделов, но не заменяет generic entity/evidence слой с merge/matching правилами.

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

- `McpServerConfig` и built-in registry остаются каноническим реестром MCP серверов;
- при запуске chat/planning run создается usage-level attachment, который ссылается на сервер из реестра;
- attachment хранит только alias, enable/disable и локальные overrides;
- при запуске chat/planning run можно передать `session parameters` для конкретного usage attachment;
- локальный MCP процесс стартует с этими параметрами только для текущей сессии;
- сохраненная конфигурация сервера при этом не меняется.

Ключевой принцип:

- мы не клонируем запись из реестра целиком;
- мы не даем "произвольную конфигурацию чего угодно";
- мы разрешаем переопределять только явно разрешенные поля, объявленные у самого MCP сервера.

Это важно для UX и валидации:

- пользователь добавляет не "абстрактные параметры", а экземпляр конкретного MCP сервера;
- UI знает, какие именно поля можно спросить для этого сервера;
- два одинаковых MCP сервера с разными параметрами считаются двумя разными usage attachments, и это нормальный сценарий.

Это особенно важно для file MCP:

- один и тот же сервер может быть определен как `sandboxed-files`;
- но для одного запуска ему задается root `C:\RepoA`;
- для другого запуска root `D:\Docs`;
- вне этих roots сервер не должен иметь доступа к файлам.

Для knowledge MCP, который работает поверх одного заранее заданного файла, `roots` не обязательны:

- достаточно передать `knowledgeFile` при старте конкретного run;
- сам MCP не должен уметь переключаться на другой файл через runtime-команды;
- ограничения вида `roots` нужны уже для отдельного file-sandbox MCP, где сервер реально ходит по файловому дереву.

### 1.1. Override-метаданные задаются рядом с регистрацией сервера

Для каждого MCP сервера в месте регистрации нужно уметь объявить:

- какие параметры можно переопределять "по месту";
- как их показать в UI;
- какой у них простой тип.

Нам не нужен сложный schema engine. Достаточно минимального описания поля:

- `key`
- `label`
- `description`
- `kind = string | int | bool`
- `required`
- `secret`

Практически этого достаточно для основных сценариев:

- путь к папке;
- путь к knowledge-файлу;
- connection string;
- логический флаг;
- числовой limit/timeout.

Для built-in MCP эти override definitions живут рядом с `BuiltInMcpServerDescriptor`.

Для external MCP серверов логика должна быть такой же:

- сервер остается записью в реестре;
- рядом с ним хранится список разрешенных overridable-параметров;
- usage-level конфигурация может задавать только их.

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

## Какие MCP серверы уже есть и какие стоит добавить

### Уже есть

1. `built-in-knowledge-book`

Его роль:

- human-readable иерархическое knowledge-хранилище;
- явная точка привязки через `knowledgeFile`;
- хороший базовый consumer для knowledge-oriented агентов;
- не замена artifact/entity/cursor слоям.

### Следующие фундаментальные серверы

1. `built-in-file-sandbox`
2. `built-in-artifact-store`
3. `built-in-cursor`
4. `built-in-cursor-agent`

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

## Как session-scoped bindings реализованы сейчас и что осталось

### Что уже есть в коде

Сейчас важная часть механики уже реализована:

- runtime binding сущность называется `McpServerSessionBinding`, а не `McpSessionBinding`;
- binding умеет адресовать сервер по `ServerId` или `ServerName` и несет `Roots` плюс произвольные `Parameters`;
- `BindingId` уже позволяет держать несколько usage attachments одного и того же сервера;
- `McpClientRequestContext` подбирает binding для конкретного сервера и включает его в fingerprint;
- `McpSessionBindingTransport` сериализует binding в `--mcp-session-binding` для built-in и локальных stdio MCP серверов;
- `McpClientService` уже создает отдельные cached client sets для разных binding-контекстов и умеет их вытеснять;
- fingerprint на runtime-стороне уже строится как hash от нормализованного binding context, то есть реальные override-значения не попадают в ключ кэша в открытом виде;
- override definitions уже объявляются через `IMcpServerDescriptor.OverrideDefinitions` и редактируются в UI для external MCP серверов;
- `McpServerBindingsEditor` уже позволяет задавать alias, roots, per-usage overrides, выбор инструментов и дублирование attachment;
- `AppToolCatalog` уже различает binding-specific tool descriptors, поэтому один и тот же MCP сервер может присутствовать несколько раз в одном runtime context;
- agentic runtime уже мерджит bindings агента и запуска;
- `KnowledgeBookStore` уже читает `knowledgeFile` из binding и намеренно игнорирует `Roots`.

### Что еще не доведено до конца

Незавершенная часть сейчас не в транспорте, а в end-to-end plumbing:

- `PlanningRunRequest` и `PlanningSessionService` уже умеют принимать run-level bindings и строить binding-specific tool catalog;
- agent-level UI уже есть, и run-level UI тоже уже выведен в single-agent chat, multi-agent chat и planning;
- нет удобного слоя валидации и пресетов по типам серверов, например `roots` для file sandbox или `knowledgeFile` для knowledge book;
- planning runtime пока не умеет вызывать заранее настроенных агентов и делегировать им bounded sub-workflow;
- нет e2e теста для planning run с bindings и проверкой приоритетов merge.

### Обновленная цель

1. Использовать существующий термин `McpServerSessionBinding` как единый контракт.
2. Опереться на уже существующую instance-based binding-модель через `BindingId`, а не вводить параллельную сущность поверх нее.
3. Добавить bindings в `PlanningRunRequest` и пропустить их в `AppToolCatalog`/tool execution path planning runtime.
4. Вывести bindings в UI на двух уровнях:
   - agent-level defaults;
   - run-level overrides для single-agent, multi-agent и planning сценариев.
5. Переиспользовать уже существующие `OverrideDefinitions` рядом с регистрацией MCP сервера и довести их использование до planning/chat run UX и валидации.
6. Если planner потребуется вызывать заранее настроенных агентов, делать это через tool/gateway слой поверх существующего agentic runtime, а не через новый planner-only step kind.

Практический вывод:

- разделение saved server config и per-run launch parameters уже есть на инфраструктурном уровне;
- оставшаяся работа это доведение до planning runtime, UX и тестов;
- поддержка двух одинаковых MCP серверов с разной конфигурацией уже заложена в `BindingId` + binding-scoped tool catalog;
- расширять нужно не transport-модель, а пути planning/runtime/UI поверх уже существующей модели.

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

Для базовой интеграции ответ: да, и сильнее, чем в исходной версии плана.

Почему:

- новый MCP сервер автоматически попадает в общий каталог инструментов;
- agentic runtime умеет ограничивать инструменты по `FunctionSettings` и уже знает про MCP bindings;
- planning runtime использует тот же каталог;
- knowledge book уже существует как готовый knowledge consumer;
- специализированный агент можно описать обычным `AgentDescription`.

### Где текущая схема еще слабая

Для production-grade knowledge server пока не хватает:

- доведения session-scoped bindings до planning runtime и UI;
- file sandbox для безопасного доступа к реальным файлам;
- long-run persisted state;
- artifact references вместо больших inline JSON результатов;
- cursor runtime и resume/progress API;
- generic entity/evidence слой поверх текущего knowledge book;
- UI для run progress и resume;
- договоренности по storage lifecycle.

### Нужна ли planner-делегация в заранее настроенных агентов

Сейчас planning runtime этого не умеет:

- `PlanStep` поддерживает только `tool` и `llm`;
- `AgentStepRunner` выполняет ad-hoc LLM шаг по prompt, а не вызов заранее настроенного `AgentDescription`;
- planner не имеет registry настроенных агентов и не может сказать "поручи это character-agent" или "вызови cursor-specialist".

Для целевого книжного сценария это не является обязательным первым шагом.

Минимально достаточный путь:

- оставить planner tool-centric;
- дать ему инструменты `file-sandbox`, `cursor`, `cursor-agent`, `knowledge-book` и затем `entity/character-knowledge`;
- завернуть bounded sub-workflow чтения книги в `cursor-agent` MCP, чтобы planner вызывал его как обычный tool step.

Если позже понадобится именно переиспользование заранее настроенных агентов, правильнее добавить не новый step kind, а gateway/tool слой поверх существующего agentic runtime, например:

- `invoke_agent(agentId, task, inputs?, bindings?)`
- `get_agent_run(runId)`
- `resume_agent_run(runId)`

Тогда planner останется tool-oriented, а делегация станет еще одним MCP-backed capability.

### Минимальный целевой сценарий: markdown-книга -> досье персонажей

Чтобы planning runtime смог пройти сценарий:

1. настроить MCP и агентов;
2. получить доступ к книге в markdown;
3. по команде "составь досье на персонажей" читать книгу порциями и постепенно обновлять knowledge/dossier state,

нужна следующая минимальная цепочка:

- источник книги: `file-sandbox` или специализированный `document-linear/document-chunks` MCP;
- long-running чтение: `cursor` + `cursor-agent`;
- накопление результата: `artifact-store`;
- целевое состояние: сначала `knowledge-book`, затем `entity-knowledge`/`character-knowledge`;
- orchestration: planning runtime с per-run bindings, чтобы один run видел конкретную книгу и конкретный knowledge file.

Практически это означает такой execution path:

1. planning run стартует с `McpServerBindings`, где заданы:
   - root до папки/файла книги;
   - `knowledgeFile` для файла досье/knowledge state.
2. planner строит план:
   - создать курсор по книге;
   - запустить cursor-agent на extraction задачи;
   - сохранить/прочитать artifact с evidence;
   - обновить knowledge/dossier store;
   - вернуть итог или продолжить scan по `nextToken`.
3. knowledge layer экспортирует итог в markdown-досье персонажей.

Что мешает этому сценарию прямо сейчас:

- нет `file-sandbox`;
- нет `cursor`/`cursor-agent`;
- нет artifact indirection;
- planning run не умеет принимать bindings;
- planning run не умеет резюмировать прогресс/resume для длинного чтения;
- knowledge book годится как output surface, но не как полноценный entity/evidence store для merge персонажей.

### Почему это хороший тест

Если система сможет устойчиво поддержать сценарий:

- cursor -> extract -> update knowledge -> query knowledge -> synthesize answer,

то это значит, что платформа уже годится не только для "вызови пару tools", а для настоящих stateful agent workflows.

## План работ

### Фаза 0. База и термины `[частично выполнено]`

Цель:

- зафиксировать уже появившиеся в коде контракты и не плодить параллельные абстракции.

Шаги:

1. Оставить этот документ как ADR/plan для cursor-driven архитектуры.
2. Зафиксировать `McpServerSessionBinding` как канонический runtime binding contract.
3. Определить общие DTO:
   - `ArtifactRef`
   - `CursorDescriptor`
   - `CursorBatch`
   - `EvidenceRecord`
   - `McpOverrideDefinition`
   - `McpUsageAttachment`
4. Зафиксировать error contract для sandbox, cursor и artifact операций.
5. Зафиксировать правило: реестр MCP серверов остается source of truth, а usage-level конфигурация хранит только разрешенные overrides.

Результат:

- следующие фазы опираются на реальные термины проекта, а не на параллельную модель.

### Фаза 1. Довести session-scoped bindings до end-to-end `[частично выполнено]`

Цель:

- завершить инфраструктуру bindings, которая уже есть в agentic runtime и transport-слое.

Шаги:

1. Сохранить текущий реестр MCP серверов как отдельный слой без дублирования конфигурации в агенте.
2. Добавить рядом с регистрацией MCP сервера список overridable-параметров:
   - `key`
   - `label`
   - `kind`
   - `required`
   - `secret`
3. Перевести usage-конфигурацию на attachments/instances, чтобы один и тот же сервер можно было добавить несколько раз с разными overrides.
4. Расширить `PlanningRunRequest` полем `McpServerBindings` или его новым instance-based эквивалентом.
5. Передавать `McpClientRequestContext` в planning runtime при построении tool catalog.
6. Вывести bindings в UI:
   - agent-level defaults;
   - run-level overrides для single-agent, multi-agent и planning запусков.
7. Добавить validation helpers/presets для `roots`, `knowledgeFile`, connection string и других типовых параметров.
8. Переделать кэш fingerprint так, чтобы он учитывал overrides через обезличенный hash нормализованных значений.
9. Добавить integration tests на:
   - merge precedence;
   - два одинаковых MCP сервера с разной конфигурацией;
   - planning + knowledge-book bindings.

Статус по шагам:

- п.1 уже реализован;
- п.2 уже реализован;
- п.3 уже в основном реализован через `BindingId`, `McpServerBindingsEditor` и binding-scoped tools;
- п.4-п.7 остаются основными незавершенными задачами;
- п.8 на runtime-стороне уже в основном закрыт, но стоит избегать raw-value cache keys и в вспомогательных UI/service слоях, если они начнут логироваться;
- из п.9 уже есть тесты на merge precedence и несколько attachment одного сервера, но нет planning e2e с bindings.

Результат:

- и chat, и planning смогут запускать один и тот же MCP сервер с разными per-run параметрами без изменения saved config;
- один агент сможет иметь несколько экземпляров одного MCP сервера, например две Knowledge Base с разными файлами.

### Фаза 2. File sandbox MCP

Цель:

- дать безопасный доступ к файловому дереву внутри разрешенных `roots`.

Шаги:

1. Реализовать built-in `file-sandbox` сервер.
2. Добавить canonical path validation.
3. Закрыть path traversal, symlink escape и UNC edge cases.
4. Добавить инструменты `list/read/grep/stat`.
5. Добавить тесты на sandbox boundaries и error contract.

Результат:

- система может работать с реальными файлами, не выходя за пределы root.

### Фаза 3. Artifact/result indirection

Цель:

- вынести большие результаты из plan state и chat state до внедрения курсоров.

Шаги:

1. Реализовать `IPlanningArtifactStore` или аналогичный application-level artifact store.
2. Ввести `preview + artifactRef` как контракт для больших результатов.
3. Изменить planning UI/state так, чтобы step details показывали preview и ссылались на artifact.
4. Определить storage lifecycle для evidence, summaries и финальных artifacts.

Результат:

- large payloads перестают раздувать `PlanStep.Result`, `PlanningSessionState` и UI.

### Фаза 4. Shared cursor runtime и `built-in-cursor`

Цель:

- получить универсальный слой batch processing поверх разных источников.

Шаги:

1. Добавить `ICursorStore`/`ICursorRuntime` в приложение.
2. Реализовать persistent cursor progress store.
3. Реализовать file cursor, chunk cursor и search-result cursor.
4. Добавить built-in `cursor` MCP:
   - `create_*_cursor`
   - `read_cursor_batch`
   - `describe_cursor`
   - `dispose_cursor`
5. Интегрировать cursor runtime с artifact store там, где источники или результаты большие.

Результат:

- источник читается порциями независимо от planning runtime и без раздувания памяти.

### Фаза 5. Cursor Agent MCP

Цель:

- внедрить bounded loop как инструмент, а не как новый step type planner.

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

- LLM сможет анализировать большие корпуса, не видя их целиком.

### Фаза 6. Эволюция knowledge layer поверх существующего knowledge book

Цель:

- сохранить уже реализованный `built-in-knowledge-book` и добавить поверх него machine-oriented knowledge слой.

Шаги:

1. Зафиксировать роль `built-in-knowledge-book`:
   - human-readable outline;
   - экспорт/редактирование;
   - удобный knowledge surface для пользователя и агентов.
2. Реализовать generic `entity-knowledge` MCP с upsert/find/export API.
3. Реализовать refresh по artifact/evidence.
4. Добавить matching/merge rules.
5. При необходимости добавить мосты между entity knowledge и knowledge book.

Результат:

- текущая knowledge book остается полезной, а extracted facts и сущности получают более правильный machine-oriented store.

### Фаза 7. Character knowledge maturity test

Цель:

- воспроизвести сценарий AITextEditor на текущей платформе без hard-coded книжной архитектуры.

Шаги:

1. Добавить `character-knowledge` MCP как specialization над entity knowledge.
2. Завести dedicated agent description.
3. Ограничить агенту инструменты:
   - file sandbox
   - cursor
   - cursor-agent
   - character-knowledge
   - при необходимости knowledge-book
4. Сделать e2e tests:
   - загрузка книги/папки с главами
   - построение досье
   - инкрементальное обновление
   - ответ по knowledge state

Результат:

- система проходит зрелый stateful MCP + agents сценарий.

### Фаза 8. Глубокая интеграция в planning runtime

Цель:

- научить planner осознанно использовать cursor/artifact/knowledge patterns после того, как они доказали свою полезность как tools.

Шаги:

1. Добавить few-shot examples для cursor/file/entity workflows.
2. Добавить в planner/replanner guidance паттерны:
   - create cursor
   - run cursor agent
   - read artifact
   - update/query knowledge
   - synthesize from artifact
3. Только при реальной необходимости позже добавить first-class step kinds:
   - `cursor`
   - `reduce`
   - `artifact`.

Результат:

- planner начнет строить long-running workflows осознанно, но без преждевременного рефакторинга plan contract.

## Что делать сначала

Рекомендуемый порядок внедрения:

1. Довести session-scoped bindings до end-to-end
2. File sandbox MCP
3. Artifact/result indirection
4. Shared cursor runtime и built-in `cursor`
5. Cursor Agent MCP
6. Эволюция knowledge layer поверх существующего knowledge book
7. Character knowledge maturity test
8. Planner-native cursor patterns

Это самый прагматичный путь, потому что:

- опирается на уже реализованный transport/binding foundation, а не пытается строить его заново;
- сохраняет существующий реестр MCP серверов как source of truth;
- не требует сложной типовой системы для override-параметров;
- дает пользу уже после первых двух фаз;
- вводит `artifactRef` до курсоров, чтобы не раздувать planning state;
- позволяет использовать текущий knowledge book как ранний knowledge surface, пока machine-oriented слой еще строится;
- не требует сразу ломать plan contract.

## Критерии успеха

Система считается успешно расширенной, если она умеет:

1. Сохранять реестр MCP серверов как отдельный слой и конфигурировать usage-level attachments только через разрешенные overrides.
2. Передавать `McpServerSessionBinding` или его instance-based эквивалент в single-agent, multi-agent и planning run без изменения saved MCP config.
3. Позволять добавить один и тот же MCP сервер несколько раз с разной конфигурацией, например две Knowledge Base с разными файлами.
4. Строить fingerprint кэша по обезличенным override-значениям без утечки путей, connection strings и других секретов.
5. Запускать sandboxed file MCP с `roots`, заданными только для текущего run, и безопасно читать сотни файлов внутри root.
6. Создавать cursor и проходить данные батчами с resume/progress.
7. Держать большие intermediate results вне `PlanStep.Result`, используя `preview + artifactRef`.
8. Использовать существующий knowledge book как knowledge surface и при этом строить machine-oriented knowledge artifacts поверх cursor-driven evidence.
9. Поддержать отдельного knowledge-агента через текущую схему `AgentDescription + FunctionSettings + Mcp bindings`.
10. Пройти e2e сценарий наподобие character dossier extraction.

## Практический вывод

Текущий проект уже содержит существенную часть фундамента, который исходный план собирался строить с нуля:

- общий tool catalog для chat и planning;
- session-scoped MCP binding transport и request context;
- override definitions и instance-based MCP attachments;
- рабочий built-in knowledge book.

Поэтому следующий шаг это не "изобрести knowledge/MCP заново", а закрыть оставшийся разрыв:

- сохранить реестр MCP серверов и перенести сложность в usage-level attachments;
- переиспользовать уже объявленные overridable-параметры рядом с регистрацией MCP сервера;
- довести bindings до planning runtime и UI;
- использовать уже существующую поддержку нескольких экземпляров одного MCP сервера на одном usage-context;
- при необходимости довести обезличивание cache keys до всех вспомогательных слоев;
- добавить file sandbox;
- ввести artifact store и `artifactRef`;
- построить cursor runtime и cursor-agent;
- при необходимости добавить tool-based gateway для вызова заранее настроенных агентов из planning runtime;
- расширить knowledge layer от knowledge book к entity/character workflows.

Именно это создаст основу и для обработки 1000 пакетов, и для анализа файловых папок, и для книжного редактирования в стиле `AITextEditor`, не ломая уже существующую архитектуру проекта.
