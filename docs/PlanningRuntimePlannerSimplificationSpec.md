# ТЗ: упрощение planning runtime и перенос промежуточных контрактов из planner в runtime

## 1. Статус документа

- Статус: proposed
- Область: `ChatClient.Api/PlanningRuntime/*`
- Цель документа: задать целевую архитектуру и поэтапный план доработки planning runtime без оглядки на обратную совместимость

## 2. Контекст

Текущая реализация planning runtime уже разделяет стадии планирования, исполнения, валидации, финальной проверки ответа и перепланирования. Основная проблема не в отсутствии дополнительных агентов, а в том, что planner перегружен описанием промежуточных контрактов:

- planner должен вручную задавать `out.schema` для `llm` и `agent` шагов;
- planner вынужден косвенно управлять `collect` и `flatten` через форму схемы;
- planner получает слишком тяжёлый prompt с полными `inputSchema` и `outputSchema` каждого инструмента;
- `binding.type` используется как ручной способ удержать shape данных, хотя это должно выводиться самим runtime;
- из-за этого initial draft часто требует repair или replan по причинам, которые не относятся к пользовательской задаче, а относятся к внутренней DSL и типизации плана.

## 3. Обязательные архитектурные принципы

- Обратную совместимость поддерживать не требуется.
- Не нужно добавлять compatibility layer, migration layer, fallback logic или переходный код ради старых форматов планов.
- Причина: у системы ещё нет реальных пользователей, поэтому чистота архитектуры важнее сохранения старых черновиков плана.
- Чистота логической структуры важнее скорости работы кода.
- Не нужно усложнять модель данных или правила исполнения ради микровыигрыша в производительности.
- При конфликте между простотой архитектуры и скоростью исполнения выбирать архитектурно более понятное решение.
- Planner должен описывать граф шагов и намерение шагов, а не вручную проектировать низкоуровневую систему контрактов.
- Runtime должен быть основным источником truth для shape, агрегации и проверки совместимости между шагами.

## 4. Цель доработки

Упростить planning DSL и перенести описание промежуточных входов и выходов из planner в runtime так, чтобы:

- planner генерировал более короткие и стабильные планы;
- количество невалидных initial draft уменьшилось;
- repair и replan исправляли реальные проблемы плана, а не внутренние артефакты типизации;
- промежуточные `llm` и `agent` шаги не требовали детального ручного описания `out.schema`, `out.aggregate` и `binding.type`;
- логика агрегации была предсказуемой и не зависела от того, насколько удачно planner угадал форму JSON-схемы.

## 5. Что входит в scope

- упрощение authored contract для `llm` и `agent` шагов;
- перенос вывода промежуточных контрактов в отдельный runtime pass;
- упрощение prompt planner, repairer и replanner;
- упрощение правил валидации intermediate step;
- изменение семантики mapped non-tool шагов;
- компактизация описания tool capabilities для planner;
- обновление тестов и телеметрии.

## 6. Что не входит в scope

- внедрение RBAC по шагам;
- добавление поля `role` в `PlanStep`;
- построение dispatcher-agent, coordinator-agent или иерархии подагентов;
- переход на multi-agent execution;
- sandbox для выполнения кода;
- поддержка старых JSON-планов;
- оптимизация под производительность до завершения логической переработки;
- замена текущего `LlmFinalAnswerVerifier` на отдельную модель в рамках этого ТЗ.

## 7. Целевая модель плана

### 7.1. Tool step

- `tool` шаг не должен задавать `out`.
- output contract tool-step всегда выводится runtime из tool catalog.

### 7.2. LLM и saved-agent step

- `llm` и `agent` шаг должны задавать `out.format`.
- Допустимые значения `out.format`: `json` или `string`.
- `out.schema` становится необязательным.
- `out.schema` разрешено только как явный override, если разработчик действительно хочет усилить контракт.
- `out.aggregate` удаляется из authored plan contract.

### 7.3. Binding

- `binding.type` больше не является обязательной частью authored plan.
- Planner не должен по умолчанию генерировать `binding.type`.
- Runtime может терпеть это поле как необязательный hint на переходном этапе внедрения, но оно не должно быть основным источником truth.

## 8. Целевая семантика mapped non-tool step

Для mapped `llm` и `agent` шагов требуется ввести простую и предсказуемую модель:

- каждый per-call result трактуется как набор logical items;
- если per-call result является массивом, runtime берёт элементы массива;
- если per-call result не является массивом, runtime трактует его как один logical item;
- итог mapped step всегда сохраняется как один плоский массив logical items;
- planner не должен выбирать между `collect` и `flatten`;
- runtime сам нормализует mapped output в плоский результат.

Следствие:

- проблема `array<object>` против `array<array<object>>` должна исчезнуть из authored DSL;
- ошибка planner в выборе aggregate не должна быть возможна, потому что authored aggregate больше не существует.

## 9. Основное проектное решение

Нужно добавить новый plan-wide runtime pass, условное название: `DerivedContractsBuilder`.

Назначение pass:

- пройти по всему плану после normalizer;
- для каждого шага вычислить derived contract;
- использовать downstream consumer-ы как основной источник требований к форме данных;
- перестать опираться только на authored `out.schema`.

Derived contract должен содержать:

- `format`;
- признак mapped step;
- derived call schema, если она выводима;
- derived final schema, если она выводима;
- источник происхождения контракта;
- признак `opaque`, если schema вывести нельзя надёжно.

Допустимые источники derived contract:

- `tool_output_schema`;
- `explicit_output_schema`;
- `derived_from_tool_consumer`;
- `derived_from_binding_path`;
- `opaque`.

## 10. Правила вывода derived contract

### 10.1. Для tool-step

- call schema и final schema берутся из `AppToolDescriptor.OutputSchema`.

### 10.2. Для non-tool step

- если есть explicit `out.schema`, она используется как override;
- если explicit schema нет, derived contract строится из требований downstream consumer-ов;
- если consumer это tool-step, требование берётся из schema целевого tool input;
- если consumer это `llm` или `agent` step, используется только структурное требование, необходимое для корректного разрешения binding path;
- если требований несколько, runtime объединяет их в одну согласованную derived schema;
- если согласованную schema построить нельзя, step помечается как `opaque`, но план не должен автоматически считаться невалидным только по этой причине.

## 11. Изменение статической валидации

Требуется ослабить validator для промежуточных non-tool шагов.

### 11.1. Что validator должен продолжать проверять жёстко

- наличие `goal`;
- непустой список `steps`;
- уникальность `step.id`;
- допустимые `kind`;
- обязательность `capabilityId` для `tool` и `agent`;
- корректность `in`;
- валидность binding syntax;
- запрет ссылок на будущие шаги;
- совместимость literal tool inputs с tool input schema;
- наличие обязательных prompt-полей у `llm` и `agent`;
- отсутствие step refs и template placeholders внутри prompt.

### 11.2. Что validator должен перестать требовать

- обязательный `out.schema` для `llm` и `agent` шага при `out.format='json'`;
- обязательный `binding.type`;
- authored `out.aggregate`.

### 11.3. Что validator должен делать условно

- проверять binding path и schema compatibility только когда derived schema действительно известна;
- не валить план статически, если shape промежуточного результата пока `opaque`;
- переносить часть ошибок формы данных на runtime validation.

## 12. Изменение runtime execution

### 12.1. PlanExecutor

Необходимо изменить сбор финального результата шага:

- для `single` шага поведение остаётся прежним;
- для mapped non-tool шагов вместо старой логики `collect/flatten` вводится auto-flat accumulation;
- runtime должен строить итоговый массив из logical items каждого успешного вызова;
- shape ошибки должны детектироваться при фактическом исполнении, а не через обязательное authored описание aggregate.

### 12.2. AgentStepRunner

Нужно упростить execution contract, который добавляется в prompt non-tool шагов:

- сохранить обязательный envelope `{"ok":...,"data":...,"error":...}`;
- убрать из prompt опору на authored `aggregate`;
- если шаг mapped, явно писать только семантику "верни один item или массив items для текущего input";
- если derived schema известна, показывать только derived call schema или item schema;
- если schema неизвестна, не выдумывать synthetic schema ради prompt.

## 13. Изменение planner prompt

Planner prompt должен стать короче и логически проще.

### 13.1. Из planner prompt нужно убрать

- требование задавать `binding.type`;
- инструкции по `out.aggregate`;
- требование всегда описывать `out.schema` для `llm` и `agent`;
- подробные правила по внутренней JSON-schema DSL, которые planner не должен проектировать вручную;
- full dump `inputSchema` и `outputSchema` каждого tool по умолчанию.

### 13.2. В planner prompt нужно оставить

- шаги и их допустимые kinds;
- правила ссылок между шагами;
- разделение external acquisition и transformation;
- запрет выдумывать capability;
- обязанность строить минимальный выполнимый план;
- compact capability summary;
- явные limits и constraints инструментов.

### 13.3. Формат capability summary

Для planner по умолчанию в prompt должны попадать:

- `toolId`;
- краткое `description` или `purpose`;
- `role`;
- `produces`;
- `constraints`;
- явные `limits`, если есть;
- `readOnly`, `destructive`, `openWorld`, `mayRequireUserInput`.

Полные JSON schema инструментов должны использоваться runtime и validator, а не planner prompt по умолчанию.

## 14. Изменение tool metadata

Нужно расширить planning metadata инструментов так, чтобы planner получал ограничения в явном человекочитаемом виде.

Требования:

- поддержать явные `limits` и `constraints` как отдельные planning metadata поля;
- для built-in и MCP tool-каталога по возможности выделять лимиты из известных схем и описаний;
- ключевые ограничения типа `search.limit <= 6` должны быть доступны planner-у без необходимости читать сырую JSON schema;
- compact formatter должен использовать metadata как первичный источник planner prompt.

## 15. Телеметрия и диагностика

Нужно расширить диагностику planning runtime.

Требуется логировать:

- источник derived contract;
- случаи `opaque` contract;
- количество initial draft, в которых отсутствует explicit intermediate schema;
- случаи runtime failure из-за неизвестного или несовместимого shape;
- случаи, когда план прошёл статическую валидацию, но упал на runtime input/output contract;
- количество replan из-за prompt/contract причин;
- количество replan из-за реальной нехватки capability или данных.

## 16. Изменения по файлам

Ожидаемые основные точки изменений:

- `ChatClient.Api/PlanningRuntime/Planning/PlanModels.cs`
- `ChatClient.Api/PlanningRuntime/Planning/PlanStepOutputContracts.cs`
- `ChatClient.Api/PlanningRuntime/Planning/PlanNormalizer.cs`
- `ChatClient.Api/PlanningRuntime/Planning/PlanValidator.cs`
- `ChatClient.Api/PlanningRuntime/Planning/LlmPlanner.cs`
- `ChatClient.Api/PlanningRuntime/Planning/LlmInitialDraftRepairer.cs`
- `ChatClient.Api/PlanningRuntime/Planning/LlmReplanner.cs`
- `ChatClient.Api/PlanningRuntime/Planning/PlanningCapabilityPromptFormatter.cs`
- `ChatClient.Api/PlanningRuntime/Execution/PlanExecutor.cs`
- `ChatClient.Api/PlanningRuntime/Agents/AgentStepRunner.cs`
- `ChatClient.Api/Services/AppToolCatalog.cs`
- новые runtime-файлы для derived contracts и их объединения

## 17. Этапы реализации

### Этап 1. Удаление ручного `binding.type`

- убрать `binding.type` из planner prompt;
- перестать требовать `binding.type` в validator;
- перестать считать `binding.type` основным runtime contract;
- обновить тесты, которые сейчас завязаны на `binding.type`.

### Этап 2. `out.schema` становится optional

- разрешить `llm` и `agent` шаги с `out.format='json'` без explicit schema;
- внедрить derived contract layer;
- обновить validator и runtime под новую модель.

### Этап 3. Удаление authored aggregate

- убрать authored `out.aggregate` из planner DSL;
- ввести auto-flat mapped semantics;
- переписать build result logic;
- обновить output contract resolution.

### Этап 4. Компактизация capability prompt

- перестать передавать planner-у полные JSON schema по умолчанию;
- перейти на compact summary с `purpose`, `produces`, `constraints`, `limits`;
- сохранить полные schema в runtime и диагностике.

### Этап 5. Диагностика и стабилизация

- добавить telemetry по derived contracts;
- провести ревизию тестов и логов;
- проверить, что replan/repair действительно переключились с "чинить DSL" на "чинить смысл плана".

## 18. Критерии приёмки

- План с `llm` шагом и `out.format='json'`, но без `out.schema`, проходит planning pipeline.
- План без `binding.type` проходит planning pipeline.
- Planner prompt больше не содержит обязательных инструкций про `binding.type`.
- Planner prompt больше не содержит обязательных инструкций про `out.aggregate`.
- Planner prompt по умолчанию не содержит полный `inputSchema` и `outputSchema` каждого инструмента.
- Mapped `llm` step, который на одних вызовах возвращает object, а на других array<object>, даёт один плоский массив.
- Ошибка planner в выборе `collect` против `flatten` больше не может быть причиной невалидности плана, потому что authored aggregate отсутствует.
- Tool-step validation остаётся строгой и продолжает проверять tool input schema.
- Repair и replan работают с новым форматом плана без compatibility-кода.
- Тесты покрывают explicit schema, derived schema, opaque shape и runtime auto-flattening.

## 19. Нефункциональные требования

- Код должен быть проще для понимания, чем текущая схема с authored intermediate contracts.
- Не допускается добавление переходных режимов, если они сохраняют старую сложную модель только ради совместимости.
- Допускается небольшое ухудшение производительности, если это радикально упрощает логическую структуру.
- Производительность можно оптимизировать только после стабилизации новой модели.
- Названия типов, методов и полей должны соответствовать принципу наименьшего удивления.

## 20. Риски и правила принятия решений

- Если возникает выбор между локальным "быстрым исправлением" и последовательным упрощением архитектуры, выбирать упрощение архитектуры.
- Если часть тестов завязана на старую authored-типизацию, тесты нужно переписывать под новую модель, а не возвращать старую модель ради прохождения тестов.
- Если derived schema нельзя вывести надёжно, допустим `opaque` режим с runtime-проверкой вместо искусственного усложнения planner DSL.
- Если на каком-то этапе старая и новая модель конфликтуют, старая модель должна быть удалена, а не сосуществовать параллельно.

## 21. Ожидаемый результат

После выполнения этого ТЗ planning runtime должен перейти от модели "planner вручную проектирует промежуточные контракты" к модели "planner описывает граф и intent, а runtime выводит и проверяет форму данных сам".

Это должно привести к следующим эффектам:

- более короткий и устойчивый planner prompt;
- меньше невалидных initial draft;
- меньше repair по искусственным причинам;
- более предсказуемая семантика mapped non-tool шагов;
- более чистая и поддерживаемая архитектура planning runtime.
