# External Review Request

## Context

Ниже описан **текущий test-only экспериментальный планировщик**, собранный поверх workflow runtime и используемый для исследования многоагентного построения executable plan. Это не production-реализация и не основной planning runtime. Эксперимент живет в тестах:

- [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:78)
- [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:52)

Цель этого memo: дать внешнему ревью максимально точное описание того, **как сейчас планировщик работает именно на уровне текстов/prompts и workflow-ролей**, где проходят границы между текстовым reasoning и доменными инструментами, и какие сбои наблюдаются на реальном примере.

## Что сейчас тестируется

Эксперимент моделирует planner не как один agent, а как group-chat workflow с несколькими ролями. Workflow создается в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:78) и имеет такую последовательность:

1. `analyzer`
2. `outline_drafter`
3. `step_materializer`
4. `contract_reviser` в цикле
5. `plan_reviewer`
6. `finalizer`

Оркестрация сделана через `PrefixCycleSuffix`, то есть сначала идут три стартовых прохода, затем циклический reviser, затем reviewer и finalizer [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:137).

Важно: в текущем эксперименте **агенты не должны выводить executable plan как JSON в chat**. Plan создается и редактируется только через shared internal plan workspace [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:295).

## Главная схема взаимодействия

Сейчас логика split такая:

1. `analyzer` делает только текстовое расширение запроса пользователя.
2. `outline_drafter` делает только текстовый coarse workflow draft.
3. `step_materializer` читает эти текстовые артефакты и уже не пишет JSON в ответ, а вызывает internal plan tools для сборки executable plan.
4. `contract_reviser` правит только одну пару соседних шагов за итерацию.
5. `plan_reviewer` валидирует весь план целиком и пытается внести минимальный fix.
6. `finalizer` ничего не правит, а лишь завершает workflow короткой текстовой нотой.

Финальный executable plan берется из shared workspace после завершения workflow run, а не из текстового ответа агента [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:222).

## Разделение внешних и внутренних инструментов

Это один из центральных моментов текущего эксперимента.

В shared user message явно написано:

- external capabilities являются **planning targets only**
- workflow agents **не могут** их вызывать напрямую
- executable plan живет в shared internal plan workspace
- internal plan-workspace tools выдаются только тем агентам, которые строят или правят план

Это формулируется в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:295).

Фактическая tool wiring по коду:

- `step_materializer` получает tools из `CreateMaterializerTools()` [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:475)
- `contract_reviser` получает `CreateContractReviserTools()` [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:475)
- `plan_reviewer` получает `CreatePlanReviewerTools()` [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:475)
- `analyzer`, `outline_drafter`, `finalizer` tools не получают [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:486)

Список внутренних domain operations сейчас задается в [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:52), [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:67) и [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:82).

## Как planner сейчас работает именно "в текстах"

### 1. Общий shared prompt

Общий user message содержит:

- исходный user request
- общие planning rules
- явное требование мыслить от user-visible deliverable
- запрет придумывать новые user requirements
- запрет требовать поля, которые listed capabilities не умеют явно вернуть
- явное разделение между external capabilities и internal plan tools
- workflow protocol по ролям
- перечисление внутренних domain operations

Это формируется в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:295).

По сути shared prompt сейчас сообщает агентам следующее:

- external tools есть, но они не исполняются на этапе planning
- итоговый plan не должен появляться как chat JSON
- planner stages должны строить plan через domain operations
- plan incomplete, пока явно не выделен `result step`

### 2. `analyzer`

Инструкция analyzer-а определена в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:352).

Он должен:

- вернуть plain text only
- не добавлять новых user requirements
- не создавать plan steps
- не упоминать concrete tool ids как обязательства
- explictly описать:
  - `Expanded Request`
  - `Goal`
  - `Deliverables`
  - `Result Expectations`
  - `Result Artifact`
  - `Constraints`
  - `Evidence Needs`
  - `Reasoning Needs`
  - `Notes For Workflow Draft`

Ключевая идея этого слоя: сделать явным, **каким должен быть конечный результат**, чтобы нижележащие стадии не забывали про него.

### 3. `outline_drafter`

Инструкция outline drafter-а определена в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:368).

Он должен:

- вернуть plain text only
- сделать coarse textual workflow draft
- не добавлять новых user requirements
- не требовать unsupported fields/capabilities
- сохранить `Result Expectations`
- сохранить `Result Artifact`
- выписать numbered coarse steps
- для каждого coarse step описать purpose/input/output
- завершить draft секциями:
  - `Result Step note`
  - `Expected Result reminder`

Ключевая идея: outline должен работать как стратегический план без executable DSL.

### 4. `step_materializer`

Инструкция materializer-а определена в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:387).

Это главный слой materialization. Сейчас текстово от него требуется:

- не выводить plan как JSON
- строить plan только через internal plan-workspace tools
- сначала читать workspace через `plan_read_structure`
- затем выставить goal через `plan_set_goal`
- явно держать в голове expected result из analyzer и outline
- **обязательно вызвать** `plan_mark_result_step`
- строить workflow через high-level domain operations:
  - `plan_add_search_step`
  - `plan_add_download_step`
  - `plan_add_extract_step`
  - `plan_add_filter_step`
  - `plan_add_rank_step`
  - `plan_add_answer_step`
- не микроменеджить bindings, если obvious source можно доверить коду
- в случае нескольких search-веток сначала вставлять `plan_add_prepare_download_inputs_step`
- перед завершением обязательно вызвать `plan_validate_full`

Это важный текущий факт для ревью: **materializer сейчас мыслит через набор заранее заданных доменных операций с жестко заданной семантикой**. То есть он не строит generic step "вызови выданный пользователем tool X", а использует заранее определенные операции вроде `plan_add_extract_step` или `plan_add_filter_step`.

Именно здесь находится ключевое архитектурное расхождение с желаемым направлением: если в целевой модели planner должен мыслить только от выданного tool catalog, то текущий experiment пока еще не соответствует этому принципу.

### 5. `contract_reviser`

Инструкция contract reviser-а определена в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:415).

Он должен:

- работать только с одной adjacent pair за turn
- выбрать pair по индексу, основанному на количестве предыдущих сообщений `contract_reviser`
- читать эту пару через `plan_read_pair`
- чинить только локальную пару
- фокусироваться на:
  - required input names
  - `map` vs `value`
  - array vs object compatibility
  - lossy projections
  - assumptions about outputs
  - prompt placeholders
  - helper wrappers
- после правки обязательно вызвать `plan_validate_pair`
- если validator вернул `ok=false`, продолжить чинить ту же пару

То есть это локальный pairwise-repair слой.

### 6. `plan_reviewer`

Инструкция plan reviewer-а определена в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:446).

Он должен:

- сначала читать текущую структуру плана
- обязательно вызывать `plan_validate_full`
- учитывать не только structural correctness, но и semantic completion
- считать план невалидным, если:
  - нет explicit result step
  - result step не ведет к deliverable
- в случае ошибки вносить smallest concrete fix через internal high-level tools
- после правки снова вызвать `plan_validate_full`

### 7. `finalizer`

Инструкция finalizer-а определена в [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:463).

Finalizer сейчас делает минимум:

- возвращает короткую plain-text completion note
- не редактирует plan
- не добавляет новых user requirements

## Как устроены внутренние domain operations сейчас

Current experiment использует shared workspace, который хранит:

- `goal`
- список `steps`
- `resultStepId`

Это видно в [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:104).

Основные operations materializer-а:

- `plan_set_goal`
- `plan_add_search_step`
- `plan_add_download_step`
- `plan_add_prepare_download_inputs_step`
- `plan_add_extract_step`
- `plan_add_filter_step`
- `plan_add_rank_step`
- `plan_add_answer_step`
- `plan_mark_result_step`
- `plan_validate_full`

Это задается в [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:52).

Operations reviser-а:

- чтение структуры и пары
- reconnection
- autowire
- update instruction
- delete / move
- add prepare-download / download
- pair validator

Это задается в [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:67).

Operations reviewer-а:

- чтение структуры / step
- autowire / reconnect / move / delete
- add extract / filter / rank / answer
- `plan_mark_result_step`
- full validator

Это задается в [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:82).

## Что делает код за LLM

Сейчас часть низкоуровневой работы уже снята с модели и делается кодом:

- `plan_add_download_step` умеет auto-wire obvious compatible source и fail-fast, если несколько competing branches [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:193)
- reasoning steps умеют брать compatible upstream source(s), если `sourceStepId` не задан [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:260)
- `plan_mark_result_step` делает explicit result marker [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:387)
- повторная пометка другого шага автоматически сдвигает `resultStepId`, то есть одновременно активен только один result step [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:391)
- validator понимает explicit result semantics и отдельно проверяет semantic completion [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:633)

Semantic validation сейчас включает как минимум:

- `result_step_missing`
- `result_step_not_user_facing`
- `intent_coverage_too_shallow`

Это видно в [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:661).

Structural validation дополнительно переопределяет старую логику terminal-step через explicit result semantics [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:1172).

## Что особенно важно для внешнего ревью

### 1. Current experiment уже не использует chat JSON plan output

Это уже исправлено. Plan не выводится агентами как JSON-сообщение. Он создается через plan-workspace tools и после прогона читается из workspace.

### 2. Но planner все еще опирается на жестко заданные semantic domain operations

Это главный текущий architectural concern.

Сейчас materializer и reviewer работают с такими operation names, как:

- `plan_add_extract_step`
- `plan_add_filter_step`
- `plan_add_rank_step`
- `plan_add_answer_step`

То есть planner мыслит не в терминах "добавить шаг, вызывающий выданный пользователем tool/capability X", а в терминах заранее зашитых planning semantics.

Если целевая архитектура должна быть такой:

- planner видит только tool catalog, выданный пользователем
- planner строит workflow из generic step operations
- семантика шага определяется доступным tool/capability

то текущий test experiment **еще не соответствует этому**.

### 3. Prompt layer уже стабильно знает про result step, но materialization это не доводит до конца

И analyzer, и outline прямо говорят про:

- answer step
- explicit result step
- `plan_mark_result_step`

Но materializer в реальных прогонах часто:

- не создает `answer` step
- не вызывает `plan_mark_result_step`
- останавливается на `extract/filter/rank`

Иными словами, проблема уже не в том, что higher-level textual intent не сформулирован. Проблема в том, что этот intent ненадежно materialize-ится в concrete step graph.

### 4. Свободный текст внутри high-level LLM steps все еще слишком хрупок

Даже при наличии domain operations materializer по-прежнему сочиняет свободный instruction text для `extract/filter/rank/answer`. Именно там сейчас появляются артефакты вроде:

- `{model}`
- `s{?}`
- `price <= 2000 (???)`
- странные или мусорные search queries

То есть JSON-in-chat убрали, но instruction-generation instability осталась.

## Фактические проблемы на последнем прогоне

Последний прогон для запроса `Посоветуй хороший робот пылесос с мойкой до 600 EUR`:

- [summary](C:/Private/OllamaChat/artifacts/planning-workflow-experiments/20260413-112814718-planning-workflow-robot-vacuum-mop-600eur.json)
- [full transcript](C:/Private/OllamaChat/artifacts/planning-workflow-experiments/20260413-112814718-planning-workflow-robot-vacuum-mop-600eur.transcript.txt)

Итог:

- `RunCount = 3`
- `ValidPlanCount = 0`
- `DistinctShapeCount = 3`

По рандам:

1. `semantic_invalid_plan`
   - `result_step_missing`
   - shape: `search -> search -> search -> llm -> download -> llm`
   - проблема: workflow даже не доходит до answer-step

2. `invalid_plan`
   - `llm_prompt_template_placeholder`
   - shape: `search -> search -> llm -> download -> llm -> llm`
   - проблема: в `extract` instruction попал мусор вроде `{model}` и `s{?}`

3. `semantic_invalid_plan`
   - `result_step_missing`
   - shape: `search -> download -> llm -> llm -> llm -> llm`
   - проблема: analyzer и outline уже явно требуют `plan_add_answer_step` и `plan_mark_result_step`, но в materialized plan их нет

Особенно показателен run 3, потому что transcript у него не пустой и на нем видно:

- analyzer очень внятно формулирует expected result
- outline очень внятно формулирует coarse plan вплоть до `Generate Answer` и `mark result`
- дальше `Step Materializer` не оставляет содержательных текстовых сообщений, а итоговый executable plan все равно не содержит explicit result step

Это хорошо локализует проблему: **текущее слабое место находится между textual outline и concrete materialization**.

## Полезные артефакты для ревью

Код:

- [PlanningWorkflowExperimentTests.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentTests.cs:78)
- [PlanningWorkflowExperimentPlanWorkspace.cs](C:/Private/OllamaChat/ChatClient.Tests/PlanningWorkflowExperimentPlanWorkspace.cs:52)

Логи последнего проблемного прогона:

- [20260413-112814718-planning-workflow-robot-vacuum-mop-600eur.transcript.txt](C:/Private/OllamaChat/artifacts/planning-workflow-experiments/20260413-112814718-planning-workflow-robot-vacuum-mop-600eur.transcript.txt)
- [20260413-112814718-planning-workflow-robot-vacuum-mop-600eur.json](C:/Private/OllamaChat/artifacts/planning-workflow-experiments/20260413-112814718-planning-workflow-robot-vacuum-mop-600eur.json)

## Основные вопросы для внешнего ревью

1. Правильна ли сама идея многоступенчатого planner-workflow вместо single planner agent?
2. Правильно ли, что analyzer и outline остаются purely textual, а concrete plan появляется только на этапе materialization?
3. Следует ли вообще отказаться от hardcoded semantic operations вроде `plan_add_extract_step` / `plan_add_filter_step` / `plan_add_rank_step`?
4. Следует ли перейти к generic domain operation вида "add workflow step that calls capability X", где planner опирается только на tool catalog, выданный пользователем?
5. Где должна жить semantic knowledge:
   - в prompts
   - в typed domain operations
   - в отдельном compiler/validator layer
   - в комбинации этих слоев?
6. Следует ли сделать creation of answer step + result marking одной атомарной операцией, либо planner должен явно вызывать две отдельные операции?
7. Насколько допустимо сейчас оставлять свободный instruction text внутри high-level LLM steps, если именно он продолжает быть источником нестабильности?

## Краткий вывод

Текущий experiment уже доказал три вещи:

1. Многоступенчатый planner полезнее single-agent planner для локализации ошибок.
2. Явный `result step` полезнее эвристики "последний шаг = результат".
3. Основная проблема теперь не в validator-е, а в том, что **materializer ненадежно переводит textual intent в executable structure**.

При этом текущая реализация все еще имеет важное архитектурное ограничение: planner materializer опирается на заранее заданные domain operations с жесткой семантикой, а не только на user-provided tool catalog. Именно это, вероятно, является главным предметом следующей архитектурной дискуссии.
