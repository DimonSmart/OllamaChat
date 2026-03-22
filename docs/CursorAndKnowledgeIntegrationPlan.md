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
- planning UI уже конфигурирует bindings через `plannerDraft.McpServerBindings`, а `PlanningSessionService` уже строит tool catalog через `planner.Agent.McpServerBindings`;
- built-in `built-in-web` уже существует как рабочий MCP сервер с `search`/`download`, так что первый cursor MVP можно строить не только поверх файлов, но и поверх web/artifact источников;
- built-in `built-in-knowledge-book` уже существует как рабочий MCP сервер с отдельным `knowledgeFile`, CRUD/search/export tools и автотестами;
- built-in `built-in-markdown-document` уже существует как file-bound MCP сервер с `sourceFile`, Markdig-based item model, pointer-based read/edit tools и автотестами;
- tool catalog уже умеет binding-aware display/description, поэтому planner и UI могут различать несколько attachment одного markdown/knowledge MCP по имени файла.

### Что это меняет для дальнейшего плана

Это означает:

- фаза про shared tool catalog уже завершена и не требует отдельной архитектурной ветки;
- фаза про session-scoped launch overrides больше не greenfield: транспорт, кэширование и planning/planner plumbing уже сделаны, остались валидация, пресеты и e2e покрытие;
- фаза про override definitions и instance-based attachments уже в значительной степени сделана, поэтому не нужно проектировать новую сущность поверх `McpServerSessionBinding`;
- knowledge MCP тоже уже не гипотеза: в проекте есть готовая knowledge book, которую можно привязывать к агенту или запуску;
- первый cursor vertical можно начинать с `built-in-web` и artifact/chunk источников, не дожидаясь обязательной готовности file-sandbox;
- новые cursor/file/domain MCP серверы надо строить поверх существующего built-in host и общего каталога, а не через отдельный planner-only или agent-only слой.

### Что пока не хватает

Ключевые пробелы на текущий момент:

- нет end-to-end покрытия для planning сценариев с реальными `McpServerBindings`, knowledge-book/file конфигурацией и проверкой merge precedence;
- нет удобного слоя валидации и пресетов по типам серверов, например `roots` для file sandbox или `knowledgeFile` для knowledge book;
- built-in `file-sandbox` пока не реализован, хотя модель bindings и `roots` уже подготовлены;
- для книжного сценария уже есть отдельный file-bound `built-in-markdown-document` с параметром `sourceFile`, но пока нет document-to-cursor bridge (`document-chunks`/`create_document_cursor`) для bounded чтения тем же runtime;
- `PlanStep.Result` и `PlanningSessionState` все еще держат результаты inline, без `preview + artifactRef`; это остается полезным улучшением, но уже не считается первым блокером для книжного cursor-сценария;
- shared cursor runtime, built-in `cursor` MCP, document-to-cursor bridge и persisted progress/resume пока отсутствуют;
- planning runtime пока не умеет вызывать заранее настроенных агентов (`AgentDescription`) как шаг плана; LLM step там сейчас только ad-hoc prompt без agent registry, а политика history compaction для длительных прогонов еще нигде не описана;
- текущий knowledge book полезен как иерархическое хранилище разделов, но не заменяет generic entity/evidence слой с merge/matching правилами.

### Практический срез на сегодня

По фактическому состоянию кода:

- фаза 0 остается частично выполненной, потому что базовые runtime термины уже есть, но `ArtifactRef`/`CursorDescriptor`/`CursorBatch` как общие DTO еще не введены;
- фаза 1 уже в основном выполнена: bindings работают в chat, multi-agent и planning, включая binding-scoped tool catalog;
- knowledge book уже не "цель", а готовая опорная capability;
- ближайший технический разрыв для cursor-driven сценариев теперь не в transport-слое, а в трех местах: planner-native вызов сохраненных агентов, history compaction внутри agentic runtime и shared cursor runtime поверх markdown-document.

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
7. `built-in-markdown-document` или `built-in-document-chunks`

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

- planning runtime и planning UI уже используют bindings end-to-end, но текущий путь идет через `Planner.Agent.McpServerBindings`, а не через отдельное поле `PlanningRunRequest`;
- agent-level UI уже есть, и run-level UI тоже уже выведен в single-agent chat, multi-agent chat и planning;
- нет dedicated integration/e2e теста для planning run с реальным built-in knowledge book, конкретным `knowledgeFile` и проверкой merge precedence;
- нет удобного слоя валидации и пресетов по типам серверов, например `roots` для file sandbox или `knowledgeFile` для knowledge book;
- planning runtime пока не умеет вызывать заранее настроенных агентов и делегировать им bounded sub-workflow;
- нет tool/gateway слоя для вызова заранее настроенных агентов из planner без введения нового step kind.

### Обновленная цель

1. Использовать существующий термин `McpServerSessionBinding` как единый контракт.
2. Опереться на уже существующую instance-based binding-модель через `BindingId`, а не вводить параллельную сущность поверх нее.
3. Сохранить текущий путь передачи planning bindings через `Planner.Agent.McpServerBindings`; отдельное поле в `PlanningRunRequest` добавлять только если позже действительно появится отдельный runtime-owner для этих bindings.
4. Считать базовый UI для agent-level и run-level bindings уже существующим и сосредоточиться на пресетах, валидации и e2e покрытии.
5. Переиспользовать уже существующие `OverrideDefinitions` рядом с регистрацией MCP сервера и довести их использование до planning/chat run UX, валидации и тестов.
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

### Этап B. Перенести markdown-document инструменты

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

- e2e покрытия, пресетов и валидации вокруг уже существующих planning/runtime bindings;
- file sandbox для безопасного доступа к реальным файлам;
- long-run persisted state;
- artifact references вместо больших inline JSON результатов;
- cursor runtime и resume/progress API;
- generic entity/evidence слой поверх текущего knowledge book;
- UI для run progress и resume;
- договоренности по storage lifecycle.

### Нужна ли planner-делегация в заранее настроенных агентов

Да. Для cursor-driven длительных операций это теперь считается целевой архитектурой.

Базовый принцип:

- planner не крутит `cursor loop` сам;
- planner вызывает заранее настроенного сохраненного агента;
- этот агент внутри себя делает tool-calling по курсору и по соседним domain tools;
- политика history compaction хранится в настройках самого сохраненного агента, а не выставляется наружу как per-step run option.

Почему это лучше для текущего проекта:

- текущий planner не приспособлен к длинному batch-by-batch loop;
- существующий agentic runtime уже умеет tool-calling loop и является естественным местом для compaction;
- разные длительные агенты будут требовать разные правила окна истории, и это ближе к identity/behavior самого агента, чем к отдельному шагу плана.

Что нужно добавить:

- новый step kind `agent` в `PlanStep`;
- planner-visible registry callable сохраненных агентов;
- resolver `AgentDescription -> ResolvedChatAgent`;
- non-streaming invoker поверх существующего agentic runtime с максимальным переиспользованием имеющегося кода;
- execution policy внутри сохраненного агента для history compaction;
- executor branch, который запускает сохраненного агента и передает ему plan inputs.

Отдельный открытый вопрос:

- нужно развести два паттерна:
- вызов сохраненного агента, который принимает именованный cursor как input и сам управляет чтением;
- executor-level fan-out, когда обычный tool принимает содержимое cursor-а, а исполнитель плана сам бежит по cursor подобно `mode=map`.

Для первой итерации приоритет отдается первому паттерну. Второй остается отдельной поздней capability.

### Минимальный целевой сценарий: markdown-книга -> досье персонажей

Чтобы planning runtime смог пройти сценарий:

1. настроить MCP и агентов;
2. получить доступ к книге в markdown;
3. по команде "составь досье на персонажей" читать книгу порциями и постепенно обновлять knowledge/dossier state,

нужна следующая минимальная цепочка:

- источник книги: специализированный `markdown-document/document-chunks` MCP с конкретным `sourceFile`;
- long-running чтение: `cursor` + заранее настроенный агент, вызванный planner-ом как отдельный `agent` step;
- накопление результата: domain-specific store/tool, который агент вызывает по месту;
- целевое состояние: сначала `knowledge-book`, затем `entity-knowledge`/`character-knowledge`;
- orchestration: planning runtime с per-run bindings, чтобы один run видел конкретную книгу и конкретный knowledge file.

Практически это означает такой execution path:

1. planning run стартует с `McpServerBindings`, где заданы:
   - `sourceFile` для markdown-книги;
   - `knowledgeFile` для файла досье/knowledge state.
2. planner строит план:
   - создать курсор по книге;
   - вызвать заранее настроенного агента для extraction/update задачи;
   - передать агенту входы (`cursorName`, `registryId` или аналогичные handles);
   - агент внутри себя читает курсор, вызывает нужные инструменты сохранения/обновления и завершает длительный проход;
   - planner получает компактный итог шага и решает, нужен ли следующий coarse-grained шаг.
3. knowledge layer экспортирует итог в markdown-досье персонажей.

Что мешает этому сценарию прямо сейчас:

- нет связки `markdown-document` -> `cursor` (`document-chunks`, `create_document_cursor`, chunk projections);
- нет `cursor` и document-to-cursor bridge;
- нет planner-native вызова заранее настроенных агентов;
- нет history compaction policy внутри сохраненного агента и соответствующего hook в agentic runtime;
- planning run уже принимает bindings через `Planner.Agent.McpServerBindings`, но нет e2e покрытия и типовых пресетов/validation для книжного сценария;
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

### Фаза 1. Довести session-scoped bindings до end-to-end `[в основном выполнено]`

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
4. Сохранить текущий путь передачи planning bindings через `Planner.Agent.McpServerBindings`, не вводя параллельное поле без явной необходимости.
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
- п.4-п.6 уже реализованы, но planning bindings сейчас идут через `Planner.Agent.McpServerBindings`, а не отдельное поле запроса;
- п.7 остается основной незавершенной задачей;
- п.8 на runtime-стороне уже в основном закрыт через hash нормализованного `McpClientRequestContext`, но стоит избегать raw-value cache keys и в вспомогательных UI/service слоях, если они начнут логироваться;
- из п.9 уже есть тесты на merge precedence и несколько attachment одного сервера, но нет planning e2e с knowledge-book/file bindings.

Результат:

- и chat, и planning уже используют один и тот же MCP plumbing для запуска MCP серверов с разными per-run параметрами без изменения saved config;
- после завершения хвоста фазы это будет закрыто еще и пресетами, валидацией и planning e2e тестами;
- один агент уже может иметь несколько экземпляров одного MCP сервера, например две Knowledge Base с разными файлами.

### Фаза 2. File sandbox MCP

Цель:

- дать безопасный доступ к файловому дереву внутри разрешенных `roots`.

Примечание:

- для ближайшего книжного сценария эта фаза не является обязательной;
- если при запуске planner/document MCP получает конкретный `sourceFile`, generic `roots` не нужны;
- `file-sandbox` остается полезным как более общий capability для папок, наборов файлов и небуквенных сценариев.

Шаги:

1. Реализовать built-in `file-sandbox` сервер.
2. Добавить canonical path validation.
3. Закрыть path traversal, symlink escape и UNC edge cases.
4. Добавить инструменты `list/read/grep/stat`.
5. Добавить тесты на sandbox boundaries и error contract.

Результат:

- система может работать с реальными файлами, не выходя за пределы root.

### Фаза 3. Planner-native делегация в сохраненных агентов

Цель:

- сделать вызов заранее настроенного агента first-class шагом planner-а и перенести внутрь него cursor loop.

Шаги:

1. Добавить новый step kind `agent` в `PlanStep` и обновить validator/serializer/planner prompt.
2. Добавить planner-visible registry callable сохраненных агентов.
3. Реализовать resolver `AgentDescription -> ResolvedChatAgent`.
4. Добавить non-streaming invoker поверх существующего agentic runtime, не дублируя его цикл.
5. Добавить executor branch для `agent` step и mapping plan inputs -> agent inputs.

Результат:

- planner получает возможность вызывать заранее настроенного агента как отдельный шаг пайплайна.

### Фаза 4. History compaction внутри сохраненного агента

Цель:

- дать сохраненному агенту возможность долго читать курсор через tool-calling без бесконтрольного роста истории.

Шаги:

1. Добавить execution policy в настройки сохраненного агента, а не в step-level run options.
2. Сначала обновить `Microsoft.Agents.AI` и `Microsoft.Extensions.AI` до последних совместимых версий и проверить, какие extension points уже дает framework.
3. Встроить history compaction в `HttpAgenticExecutionRuntime` с максимальным переиспользованием существующего agentic loop.
4. Разрешить максимум нейтральную системную пометку, что история чата может быть сокращена; не добавлять автоматических рекомендаций по сохранению состояния.
5. Отдельно оставить на потом лимиты tool-вызовов как возможную часть настроек агента.

Результат:

- сохраненный агент может проходить длинный cursor-driven workflow в скользящем окне истории.

### Фаза 5. Shared cursor runtime и document-to-cursor bridge

Цель:

- дать сохраненным агентам stateful cursor source поверх markdown-document и других источников.

Шаги:

1. Добавить `ICursorStore`/`ICursorRuntime` в приложение.
2. Реализовать persistent cursor progress store.
3. Добавить built-in `cursor` MCP:
   - `create_*_cursor`
   - `read_cursor_batch`
   - `describe_cursor`
   - `dispose_cursor`
4. В первой книжной итерации добавить bridge `markdown-document -> cursor`:
   - `create_document_cursor(...)`
   - `read_document_chunk(...)`
   - именованные cursor handles для передачи в `agent` step
5. Отдельно сохранить открытым более поздний паттерн executor-level fan-out по cursor input, аналогичный `mode=map`.

Результат:

- заранее настроенный агент получает порционное чтение документа как входную capability.

### Фаза 6. Markdown document layer для книги `[частично выполнено]`

Цель:

- дать системе не только knowledge-output surface, но и отдельный слой адресного чтения и редактирования markdown-книги.
- в первой книжной итерации работать от конкретного `sourceFile`, переданного через binding, без обязательного `roots`.

Шаги:

1. `built-in-markdown-document` уже добавлен.
   - session-scoped параметр: `sourceFile`
   - есть Markdig-based parse, item pointers, read/search/export и bounded edit operations
   - еще нет `document-chunks`/`create_document_cursor` поверх общего cursor runtime
2. Определить стабильные pointer/reference модели для книги:
   - chapter/section pointer;
   - paragraph pointer;
   - chunk pointer;
   - optional semantic pointer.
3. Инструменты чтения/навигации частично уже есть:
   - `doc_get_context`
   - `doc_list_headings`
   - `doc_get_section`
   - `doc_list_items`
   - `doc_export_markdown`
   - еще нужны `read_document_chunk(...)` и `create_document_cursor(...)`
4. Bounded edit operations поверх markdown уже частично есть через `doc_apply_operations`.
   - этого достаточно для item-level replace/insert/remove
   - позже можно добавить более семантические alias-tools уровня section/chunk
5. Определить связь document pointers с cursor runtime и artifact store, чтобы planner мог:
   - читать книгу кусочками;
   - копить evidence отдельно;
   - применять адресные изменения обратно в документ.
6. Добавить e2e сценарий "прочитай главу -> обнови раздел досье -> внеси адресную правку в markdown".

Результат:

- платформа получает прямую поддержку AITextEditor-подобного сценария работы с markdown-книгой, а не только побочный export через knowledge-book.

### Фаза 7. Эволюция knowledge layer поверх существующего knowledge book

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

### Фаза 8. Character knowledge maturity test

Цель:

- воспроизвести сценарий AITextEditor на текущей платформе без hard-coded книжной архитектуры.

Шаги:

1. Добавить `character-knowledge` MCP как specialization над entity knowledge.
2. Завести dedicated agent description.
3. Ограничить агенту инструменты:
   - markdown-document
   - cursor
   - character-knowledge
   - при необходимости knowledge-book
4. Сделать e2e tests:
   - загрузка книги/папки с главами
   - построение досье
   - инкрементальное обновление
   - ответ по knowledge state

Результат:

- система проходит зрелый stateful MCP + agents сценарий.

### Фаза 9. Глубокая интеграция в planning runtime

Цель:

- научить planner осознанно использовать `agent` steps, cursor patterns и knowledge workflows после того, как они доказали свою полезность.

Шаги:

1. Добавить few-shot examples для cursor/file/entity workflows и planner-driven agent delegation.
2. Добавить в planner/replanner guidance паттерны:
   - create cursor
   - invoke saved agent
   - update/query knowledge
   - synthesize final answer
3. Отдельно и только при реальной необходимости позже рассмотреть executor-level fan-out и специальные step kinds:
   - `cursor`
   - `reduce`
   - `artifact`.

Результат:

- planner начнет строить long-running workflows осознанно, но без преждевременного рефакторинга plan contract.

## Что делать сначала

### Согласованный scope ближайшей итерации

Делаем первые пять блоков, которые непосредственно двигают сценарий "markdown-книга -> досье персонажей":

1. хвост по session-scoped bindings: presets/validation + planning e2e;
2. planner-native `agent` step и callable saved agents;
3. execution policy сохраненного агента + history compaction внутри agentic runtime;
4. non-streaming invocation поверх существующего agentic runtime и executor branch для `agent` step;
5. shared cursor runtime вместе с document-to-cursor bridge поверх уже существующего `built-in-markdown-document`.

Что сознательно не включаем в этот scope:

- generic `file-sandbox` с `roots`;
- папочные/мультифайловые сценарии;
- executor-level fan-out по cursor input;
- отдельный generic `cursor-reader` агент как готовый к использованию артефакт без domain tools;
- full entity/character maturity layer поверх knowledge artifacts.

Рекомендуемый порядок внедрения:

1. Закрыть хвост фазы 1: validation/presets + planning e2e для bindings
2. Planner-native `agent` step + callable saved agents
3. History compaction внутри сохраненного агента с максимальным переиспользованием agentic runtime
4. Non-streaming invocation и executor branch для `agent` step
5. Shared cursor runtime MVP + document-to-cursor bridge поверх существующего `built-in-markdown-document`
6. Эволюция knowledge layer поверх существующего knowledge book
7. Character knowledge maturity test
8. Generic file sandbox MCP + file cursor как расширение cursor runtime
9. Artifact/result indirection как отдельное улучшение состояния и UI
10. Planner-native cursor patterns и, при необходимости, executor-level fan-out

Это самый прагматичный путь, потому что:

- опирается на уже реализованный transport/binding foundation, а не пытается строить его заново;
- закрывает последний разрыв в bindings через тесты и UX, а не через новую модель;
- переиспользует уже существующий agentic runtime как движок исполнения сохраненного агента, а не строит второй цикл;
- переносит history compaction туда, где реально живет tool-calling loop;
- дает первый cursor vertical поверх `built-in-markdown-document`, который уже есть в проекте;
- сохраняет существующий реестр MCP серверов как source of truth;
- не требует выставлять history compaction наружу как отдельный step-level DSL;
- дает пользу уже после первых четырех фаз;
- для книжного кейса использует прямой `sourceFile` binding вместо избыточного generic `roots`;
- отделяет прямую работу с markdown-документом от knowledge-store, что ближе к модели `AITextEditor`;
- не блокирует первую итерацию на `artifactRef`, который можно ввести позже отдельной фазой;
- позволяет использовать текущий knowledge book как ранний knowledge surface, пока machine-oriented слой еще строится;
- осознанно расширяет plan contract ровно в одной точке: новым `agent` step.

## Критерии успеха

Система считается успешно расширенной, если она умеет:

1. Сохранять реестр MCP серверов как отдельный слой и конфигурировать usage-level attachments только через разрешенные overrides.
2. Передавать `McpServerSessionBinding` или его instance-based эквивалент в single-agent, multi-agent и planning run без изменения saved MCP config.
3. Позволять добавить один и тот же MCP сервер несколько раз с разной конфигурацией, например две Knowledge Base с разными файлами.
4. Строить fingerprint кэша по обезличенным override-значениям без утечки путей, connection strings и других секретов.
5. Запускать sandboxed file MCP с `roots`, заданными только для текущего run, и безопасно читать сотни файлов внутри root.
6. Создавать cursor и проходить данные батчами с resume/progress.
7. Вызывать заранее настроенного сохраненного агента из planner-а как отдельный `agent` step с входами, резолвящимися из результатов предыдущих шагов.
8. Поддерживать history compaction как внутреннюю execution policy сохраненного агента при его запуске через planning runtime.
9. Держать большие intermediate results вне `PlanStep.Result`, используя `preview + artifactRef`, когда это действительно понадобится отдельным сценариям.
10. Использовать существующий knowledge book как knowledge surface и при этом строить machine-oriented knowledge artifacts поверх cursor-driven evidence.
11. Поддержать отдельного knowledge-агента через текущую схему `AgentDescription + FunctionSettings + Mcp bindings`.
12. Пройти e2e сценарий наподобие character dossier extraction.

## Практический вывод

Текущий проект уже содержит существенную часть фундамента, который исходный план собирался строить с нуля:

- общий tool catalog для chat и planning;
- session-scoped MCP binding transport и request context;
- override definitions и instance-based MCP attachments;
- рабочий built-in knowledge book.

Поэтому следующий шаг это не "изобрести knowledge/MCP заново", а закрыть оставшийся разрыв:

- сохранить реестр MCP серверов и перенести сложность в usage-level attachments;
- переиспользовать уже объявленные overridable-параметры рядом с регистрацией MCP сервера;
- не переделывать bindings заново, а добить presets, validation и planning e2e вокруг уже существующего plumbing;
- использовать уже существующую поддержку нескольких экземпляров одного MCP сервера на одном usage-context;
- при необходимости довести обезличивание cache keys до всех вспомогательных слоев;
- добавить first-class `agent` step и planner-visible registry callable сохраненных агентов;
- переиспользовать существующий agentic runtime как execution core для `agent` step;
- добавить history compaction как внутреннюю настройку сохраненного агента, а не как внешний step option;
- обновить `Microsoft.Agents.AI` и `Microsoft.Extensions.AI` до последних совместимых версий перед встраиванием compaction;
- построить cursor runtime и document-to-cursor bridge поверх уже существующего `built-in-markdown-document`;
- позже вернуться к `artifactRef` как отдельному улучшению состояния и UI;
- добавить file sandbox как отдельное расширение общего cursor runtime, а не как обязательную часть первой книжной итерации;
- расширить knowledge layer от knowledge book к entity/character workflows.

Именно это создаст основу и для обработки 1000 пакетов, и для анализа файловых папок, и для книжного редактирования в стиле `AITextEditor`, не ломая уже существующую архитектуру проекта.
