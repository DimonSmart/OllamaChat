# Agent Orchestrations: Architecture And Execution Spec

## 1. Цель документа

Этот документ фиксирует целевой подход к расширению текущей вкладки `Handoff Workflows` в полноценную вкладку `Agent Orchestrations`, а также задает самодостаточное ТЗ на реализацию.

Документ должен быть достаточным для того, чтобы по нему можно было:

- понять архитектурную идею;
- увидеть противоречия с текущей handoff-only реализацией;
- выполнить рефакторинг без дополнительных устных пояснений;
- поэтапно довести решение до рабочего состояния.

## 2. Основная идея

### 2.1. Что считается правильной архитектурой

В системе должен существовать **единый generic pipeline**:

1. пользовательский код описывает оркестрацию;
2. код компилируется в **нашу typed definition**, а не в framework runtime object;
3. definition materialize-ится: подставляются saved agents, override-ы, runtime bindings;
4. definition преобразуется в конкретный `Microsoft.Agents.AI.Workflows.Workflow`;
5. далее этот `Workflow` запускается общим runner-ом;
6. UI получает поток сообщений и показывает результат, не зная лишних деталей конкретного orchestration pattern.

### 2.2. Где должна быть generic-часть

Generic должны быть:

- authoring surface, на котором пользователь описывает workflow;
- compiler output;
- start request / session lifecycle;
- materializer;
- chat session host;
- UI страницы;
- сохранение и загрузка workflow definition.

### 2.3. Где специфика orchestration pattern неизбежна

Специфика конкретного orchestration pattern не должна торчать везде, но **она неизбежно существует** в одной узкой точке:

- при превращении нашей definition в framework runtime `Workflow`.

Именно там pattern-specific код легитимен:

- `handoff` требует граф handoff-переходов;
- `group-chat` требует manager factory;
- `sequential` требует линейного порядка;
- `concurrent` требует набора участников и стратегии aggregation.

Следовательно, правильная цель не в том, чтобы “вообще нигде не было видно тип оркестратора”, а в том, чтобы:

- **тип оркестратора был скрыт почти везде**;
- **pattern-specific логика была локализована в одном runtime factory / strategy layer**.

## 3. Почему нельзя компилировать пользовательский код сразу в framework `Workflow`

Нельзя строить архитектуру так, чтобы пользовательский скрипт возвращал уже готовый `Microsoft.Agents.AI.Workflows.Workflow`.

Причины:

1. runtime `AIAgent` появляется только после выбора модели и runtime binding-ов;
2. `TaskSessionStore` и session id подставляются только во время запуска;
3. `GroupChatManager` и custom managers требуют runtime registry/factory;
4. `Concurrent` aggregation в ряде случаев является кодовой стратегией, а не просто данными;
5. framework `Workflow` неудобен для:
   - сохранения;
   - загрузки;
   - инспекции в UI;
   - pattern-specific preview;
   - тестов на compile/save/load.

Правильный подход:

- пользовательский код возвращает **нашу orchestration definition**;
- framework `Workflow` строится только на runtime этапе.

## 4. Принципы проектирования

При реализации нужно соблюдать следующие принципы:

1. Не добавлять compatibility-код без необходимости.
2. Не дублировать handoff-архитектуру для каждого нового pattern.
3. Использовать явные typed models вместо неструктурированных словарей.
4. Названия классов и полей должны быть предсказуемыми.
5. Public surface должен быть generic, а pattern-specific детали должны находиться в strategy/factory layer.
6. UI не должен знать, как именно framework строит `Workflow`.
7. Runtime runner не должен быть назван `Handoff...`, если он по факту должен запускать любой orchestration.

## 5. Целевая архитектура

### 5.1. Слой authoring / DSL

Пользователь должен описывать workflow через **единый generic builder**, а не через handoff-only builder.

Целевой стиль:

```csharp
var workflow = WorkflowDefinitionBuilder
    .New("philosopher-battle", "Philosopher Battle")
    .Description("Structured debate between philosophers.")
    .RunAutonomously(maxAutomaticTurns: 14, completionPhase: "complete", completionSummaryLabel: "final")
    .RequireText("opening_topic", "Opening Topic")
    .Agent("host", agent => agent
        .Role("Host")
        .UseDraft(...))
    .Agent("kant", agent => agent
        .Role("Kant")
        .UseSavedTemplate("Immanuel Kant"))
    .Agent("nietzsche", agent => agent
        .Role("Nietzsche")
        .UseSavedTemplate("Friedrich Nietzsche"))
    .UseGroupChat(groupChat => groupChat
        .Participants("host", "kant", "nietzsche")
        .UseCustomManager("philosopher-debate", maximumIterations: 14))
    .Build();
```

### 5.2. Internal typed definitions

Внутри системы должны существовать отдельные pattern-specific definition types:

- `AgentWorkflowDefinition` для handoff;
- `GroupChatWorkflowDefinition`;
- `SequentialWorkflowDefinition`;
- `ConcurrentWorkflowDefinition`.

Все они должны реализовывать общий контракт:

- `IOrchestrationWorkflowDefinition`.

### 5.3. Compiler output

Compiler должен возвращать:

- `Kind`;
- common metadata;
- typed workflow payload;
- общий `Workflow` reference как `IOrchestrationWorkflowDefinition`.

Compiler не должен прошивать `Kind = handoff`.

### 5.4. Materializer

Materializer должен быть generic:

- принимать `IOrchestrationWorkflowDefinition`;
- materialize-ить агентов независимо от orchestration pattern;
- возвращать definition того же pattern.

Pattern-specific данные при этом не должны теряться.

### 5.5. Runtime workflow factory

Должен существовать узкий слой, который знает, как по typed definition собрать framework runtime `Workflow`.

Целевой контракт:

```csharp
public interface IOrchestrationRuntimeWorkflowBuilder
{
    bool CanBuild(IOrchestrationWorkflowDefinition workflow);

    Workflow Build(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyDictionary<string, AIAgent> agentsById,
        OrchestrationRuntimeBuildContext context);
}
```

Или эквивалентная factory/registry схема.

Pattern-specific реализации:

- `HandoffRuntimeWorkflowBuilder`
- `GroupChatRuntimeWorkflowBuilder`
- `SequentialRuntimeWorkflowBuilder`
- `ConcurrentRuntimeWorkflowBuilder`

### 5.6. Session host / runner

Должен существовать общий сервис, условно:

- `IOrchestrationWorkflowSessionService`
- `OrchestrationWorkflowChatSessionService`

Он отвечает за:

- старт сессии;
- task session setup;
- materialization;
- runtime agent creation;
- запуск `Workflow`;
- streaming events;
- общий chat lifecycle;
- сохранение transcript;
- cancellation;
- kickoff для autonomous mode.

Он **не должен** быть handoff-specific.

### 5.7. UI

Страница должна стать orchestration-aware:

- generic page state;
- generic start form;
- generic source editor;
- pattern-specific preview blocks.

Страница не должна зависеть от наличия `Handoffs`.

### 5.8. Custom group chat managers

Для `group-chat` должен существовать registry фабрик кастомных менеджеров.

Целевой контракт:

```csharp
public interface IGroupChatManagerFactory
{
    string Key { get; }

    GroupChatManager Create(IReadOnlyList<AIAgent> agents, GroupChatWorkflowDefinition workflow);
}
```

Для философских дебатов должна существовать отдельная реализация, например:

- `PhilosopherDebateGroupChatManagerFactory`
- `PhilosopherDebateGroupChatManager`

## 6. Что не так в текущей реализации

На момент написания документа текущая реализация противоречит целевому подходу в следующих точках:

1. `WorkflowDefinitionCompiler` исторически прошит под `handoff`.
2. `WorkflowAgentDraftMaterializer` materialize-ит только `AgentWorkflowDefinition`.
3. `HandoffWorkflowSessionStartRequest` описывает только handoff-сценарий.
4. `IHandoffWorkflowSessionService` и `HandoffWorkflowChatSessionService` по имени и по смыслу привязаны к handoff.
5. Runtime `BuildWorkflow(...)` внутри session service использует только `CreateHandoffBuilderWith(...)`.
6. Страница `AgentWorkflows.razor` зависит от `compiledWorkflow.HandoffWorkflow`.
7. Persistence раньше нормализовал `Kind` только к `handoff`.

Это не значит, что текущая handoff orchestration плохая. Это значит, что ее специфика протекла во весь execution path, хотя должна была остаться локальной.

## 7. Целевой публичный API

### 7.1. Generic builder

Требование:

- новый public authoring API должен быть generic.

Допускается:

- временно сохранить существующие `HandoffWorkflowDefinitionBuilder` и `GroupChatWorkflowDefinitionBuilder` как внутренние/промежуточные адаптеры;
- но шаблоны, документация и новая public surface должны переехать на generic builder.

### 7.2. Целевые generic sub-builders

Пример структуры:

- `WorkflowDefinitionBuilder`
- `HandoffConfigurationBuilder`
- `GroupChatConfigurationBuilder`
- `SequentialConfigurationBuilder`
- `ConcurrentConfigurationBuilder`

Пример handoff:

```csharp
.UseHandoff(handoff => handoff
    .StartWith("triage")
    .Handoff("triage", "specialist", "route to specialist")
    .Fallback("specialist", "triage"))
```

Пример sequential:

```csharp
.UseSequential(sequential => sequential
    .Order("stage1", "stage2", "stage3"))
```

Пример concurrent:

```csharp
.UseConcurrent(concurrent => concurrent
    .Participants("research", "review", "summary")
    .AggregateLastMessagePerAgent())
```

Пример group chat:

```csharp
.UseGroupChat(groupChat => groupChat
    .Participants("host", "kant", "nietzsche", "judge")
    .UseCustomManager("philosopher-debate", maximumIterations: 14))
```

## 8. ТЗ на реализацию

Ниже находится **самодостаточное ТЗ**. По нему можно выполнить изменения без обращения к дополнительным пояснениям.

### 8.1. Общие требования

Исполнитель обязан:

1. Не ломать текущий build.
2. Не использовать `handoff` как generic имя новых сущностей.
3. Не вводить новые абстракции без четкой ответственности.
4. Не плодить compatibility wrappers без необходимости.
5. На каждом этапе держать тесты зелеными.

Если какой-то пункт уже реализован частично, исполнитель обязан:

- проверить соответствие этому документу;
- доработать до целевого состояния;
- только после этого переходить к следующему шагу.

---

### 8.2. Шаг 1. Нормализовать терминологию и границы ответственности

#### Требуемые изменения

1. Ввести общие session abstractions:
   - `IOrchestrationWorkflowSessionService`
   - `OrchestrationWorkflowSessionStartRequest`
   - `OrchestrationWorkflowStartInputValue`

2. Перестать использовать `Handoff...` в generic execution path.

3. Переименовать UI-термины:
   - `Handoff Workflows` -> `Agent Orchestrations`
   - `Workflow Chat` оставить generic

#### Файлы

Обновить или создать:

- `ChatClient.Api/Client/Services/Agentic/IOrchestrationWorkflowSessionService.cs`
- `ChatClient.Api/Client/Services/Agentic/OrchestrationWorkflowSessionModels.cs`
- `ChatClient.Api/Client/Services/Agentic/OrchestrationWorkflowChatSessionService.cs`
- `ChatClient.Api/Client/Pages/AgentWorkflows.razor`
- `ChatClient.Api/Client/Layout/NavMenu.razor`
- `ChatClient.Api/ServiceCollectionExtensions.cs`

#### Критерий готовности

- generic session API больше не содержит `handoff` в имени;
- UI больше не позиционирует страницу как handoff-only.

---

### 8.3. Шаг 2. Завершить generic authoring API

#### Требуемые изменения

1. Создать public generic builder:
   - `WorkflowDefinitionBuilder`

2. Реализовать pattern-specific конфигурационные sub-builder-ы:
   - `HandoffConfigurationBuilder`
   - `GroupChatConfigurationBuilder`
   - `SequentialConfigurationBuilder`
   - `ConcurrentConfigurationBuilder`

3. Поддержать единый `.Build()` с возвратом `IOrchestrationWorkflowDefinition`.

4. Перевести новые шаблоны workflow code templates на generic builder.

#### Допустимое промежуточное решение

Разрешено временно использовать существующие pattern-specific builders внутри generic builder.

#### Файлы

Создать или обновить:

- `ChatClient.Api/AgentWorkflows/WorkflowDefinitionBuilder.cs`
- `ChatClient.Api/AgentWorkflows/HandoffConfigurationBuilder.cs`
- `ChatClient.Api/AgentWorkflows/GroupChatConfigurationBuilder.cs`
- `ChatClient.Api/AgentWorkflows/SequentialConfigurationBuilder.cs`
- `ChatClient.Api/AgentWorkflows/ConcurrentConfigurationBuilder.cs`
- `ChatClient.Api/AgentWorkflows/WorkflowCodeTemplates.cs`

#### Критерий готовности

- пользователь может описать любой supported orchestration через единый builder;
- code templates не завязаны на handoff-only DSL.

---

### 8.4. Шаг 3. Сделать materializer generic

#### Требуемые изменения

1. Обновить `IWorkflowAgentDraftMaterializer` так, чтобы он принимал `IOrchestrationWorkflowDefinition`.
2. Реализовать materialization для всех supported definition types.
3. Pattern-specific поля не должны теряться.

#### Особое требование

ShortName/Id mapping для runtime agent должен оставаться предсказуемым.

#### Файлы

Обновить:

- `ChatClient.Api/AgentWorkflows/WorkflowAgentDraftMaterializer.cs`

При необходимости создать pattern-specific helpers.

#### Критерий готовности

- любой compiled workflow после materialization сохраняет свой `Kind` и pattern-specific configuration.

---

### 8.5. Шаг 4. Вынести pattern-specific runtime building в отдельный слой

#### Требуемые изменения

1. Создать общий runtime build context, например:
   - `OrchestrationRuntimeBuildContext`

2. Создать registry/factory layer для сборки framework `Workflow`.

3. Реализовать отдельные builders:
   - `HandoffRuntimeWorkflowBuilder`
   - `GroupChatRuntimeWorkflowBuilder`
   - `SequentialRuntimeWorkflowBuilder`
   - `ConcurrentRuntimeWorkflowBuilder`

4. Удалить pattern-specific `BuildWorkflow(...)` из общего session host.

#### Файлы

Создать:

- `ChatClient.Api/AgentWorkflows/Runtime/IOrchestrationRuntimeWorkflowBuilder.cs`
- `ChatClient.Api/AgentWorkflows/Runtime/OrchestrationRuntimeBuildContext.cs`
- `ChatClient.Api/AgentWorkflows/Runtime/HandoffRuntimeWorkflowBuilder.cs`
- `ChatClient.Api/AgentWorkflows/Runtime/GroupChatRuntimeWorkflowBuilder.cs`
- `ChatClient.Api/AgentWorkflows/Runtime/SequentialRuntimeWorkflowBuilder.cs`
- `ChatClient.Api/AgentWorkflows/Runtime/ConcurrentRuntimeWorkflowBuilder.cs`

#### Критерий готовности

- общий runner больше не знает, как строится handoff/group chat/sequential/concurrent;
- эта логика живет только в runtime factory layer.

---

### 8.6. Шаг 5. Переписать runner как generic orchestration host

#### Требуемые изменения

1. Вынести из текущего `HandoffWorkflowChatSessionService` общий lifecycle код:
   - task session setup
   - start input normalization
   - runtime agent creation
   - execution loop
   - event streaming
   - transcript persistence
   - cancellation

2. Новый сервис должен принимать generic start request с `IOrchestrationWorkflowDefinition`.

3. `ChatStrategyName` должен быть dynamic:
   - `HandoffWorkflow`
   - `GroupChatWorkflow`
   - `SequentialWorkflow`
   - `ConcurrentWorkflow`
   - либо единый `AgentOrchestration` + `Kind`

#### Файлы

Создать или обновить:

- `ChatClient.Api/Client/Services/Agentic/OrchestrationWorkflowChatSessionService.cs`
- `ChatClient.Api/Client/Services/Agentic/OrchestrationWorkflowChatViewModelService.cs`
- `ChatClient.Api/Client/Services/Agentic/IOrchestrationWorkflowChatViewModelService.cs`

Текущий handoff service должен быть либо:

- удален;
- либо превращен во внутренний legacy wrapper на время миграции.

#### Критерий готовности

- один session host запускает любой supported orchestration через runtime strategy layer.

---

### 8.7. Шаг 6. Подготовить и упростить UI под generic orchestration

#### Требуемые изменения

1. Страница должна работать с `CompiledWorkflowDefinition.Workflow`.
2. Страница должна показывать общие секции:
   - source
   - metadata
   - start inputs
   - agents
   - chat

3. Страница должна показывать pattern-specific preview:
   - handoff edges
   - sequential order
   - concurrent participants + aggregation
   - group-chat participants + manager info

4. Страница не должна обращаться к `HandoffWorkflow` без type check.

#### Файлы

Обновить:

- `ChatClient.Api/Client/Pages/AgentWorkflows.razor`

При необходимости создать partial components:

- `ChatClient.Api/Client/Components/AgentWorkflows/HandoffWorkflowPreview.razor`
- `ChatClient.Api/Client/Components/AgentWorkflows/GroupChatWorkflowPreview.razor`
- `ChatClient.Api/Client/Components/AgentWorkflows/SequentialWorkflowPreview.razor`
- `ChatClient.Api/Client/Components/AgentWorkflows/ConcurrentWorkflowPreview.razor`

#### Критерий готовности

- один и тот же UI способен загрузить, скомпилировать, показать и стартовать workflow любого supported kind.

---

### 8.8. Шаг 7. Реализовать `Group Chat` как первый реально исполняемый новый pattern

#### Требуемые изменения

1. Реализовать `GroupChatRuntimeWorkflowBuilder` через:
   - `AgentWorkflowBuilder.CreateGroupChatBuilderWith(...)`

2. Реализовать manager registry:
   - `IGroupChatManagerFactory`
   - registry/factory lookup по `ImplementationKey`

3. Реализовать custom manager для философских дебатов.

#### Минимальная реализация дебатного manager-а

Manager обязан:

1. вести phase machine;
2. deterministically выбирать следующего спикера;
3. ограничивать число итераций;
4. корректно завершать debate;
5. не допускать бесконечных циклов.

#### Базовый состав участников

- `host`
- `kant`
- `nietzsche`
- `judge`

#### Базовые фазы

1. opening by host
2. opening by Kant
3. opening by Nietzsche
4. rebuttal loop
5. closing by Kant
6. closing by Nietzsche
7. verdict by judge

#### Файлы

Создать:

- `ChatClient.Api/AgentWorkflows/GroupChat/IGroupChatManagerFactory.cs`
- `ChatClient.Api/AgentWorkflows/GroupChat/GroupChatManagerRegistry.cs`
- `ChatClient.Api/AgentWorkflows/GroupChat/PhilosopherDebateGroupChatManagerFactory.cs`
- `ChatClient.Api/AgentWorkflows/GroupChat/PhilosopherDebateGroupChatManager.cs`

Обновить:

- `ChatClient.Api/AgentWorkflows/WorkflowCodeTemplates.cs`
- `ChatClient.Api/Services/Seed/WorkflowDefinitionSeeder.cs`

#### Критерий готовности

- философские дебаты можно реально запускать через `group-chat`, а не только компилировать.

---

### 8.9. Шаг 8. Добавить `sequential` и `concurrent`

#### Sequential

Требуется:

- поддержать typed definition;
- runtime builder через `AgentWorkflowBuilder.BuildSequential(...)`;
- preview в UI;
- sample template.

#### Concurrent

Требуется:

- поддержать typed definition;
- runtime builder через `AgentWorkflowBuilder.BuildConcurrent(...)`;
- базовую aggregation strategy;
- preview в UI;
- sample template.

#### Критерий готовности

- все четыре orchestration kinds могут быть скомпилированы, показаны в UI и запущены через единый session host.

---

### 8.10. Шаг 9. Тесты

#### Обязательные тесты

1. Compiler tests:
   - handoff
   - group-chat
   - sequential
   - concurrent

2. Persistence tests:
   - normalizing `Kind`
   - create/update/load each kind

3. Materializer tests:
   - saved template resolution
   - overrides
   - сохранение pattern-specific fields

4. Runtime builder tests:
   - handoff graph
   - group chat participants and manager
   - sequential order
   - concurrent aggregation defaults

5. Session host tests:
   - common start flow
   - autonomous kickoff
   - transcript persistence
   - cancellation

6. UI tests или component-level smoke tests там, где это практично.

7. At least one real execution smoke test:
   - handoff
   - group-chat

#### Файлы

Обновить или создать новые тесты в:

- `ChatClient.Tests/`

#### Критерий готовности

- все новые generic abstractions покрыты тестами;
- regression на handoff не допущен.

---

### 8.11. Шаг 10. Cleanup

После завершения миграции исполнитель обязан:

1. удалить или минимизировать временные compatibility wrappers;
2. удалить handoff-only naming из generic path;
3. привести документацию и code templates в соответствие новой архитектуре;
4. оставить только те pattern-specific элементы, которые действительно нужны в runtime strategy layer.

## 9. Критерии готовности всей задачи

Задача считается завершенной, когда выполнены все пункты ниже:

1. Вкладка больше не handoff-only.
2. Пользовательский workflow authoring использует generic builder surface.
3. Compiler принимает любой supported orchestration kind.
4. Materializer generic.
5. Session host generic.
6. Pattern-specific runtime logic локализована в strategy/factory layer.
7. `Group Chat` для философских дебатов реально исполняется.
8. `Sequential` и `Concurrent` тоже поддерживаются.
9. Тесты подтверждают отсутствие регрессии handoff.

## 10. Что не входит в эту задачу

Следующее явно вне scope текущей работы:

- поддержка `Magentic` в C#, пока framework этого не поддерживает;
- сохранение raw framework `Workflow` objects;
- универсальный visual graph editor для всех orchestration kinds;
- сложные human-in-the-loop UI для plan review;
- внешняя backward compatibility ради старого handoff naming, если она не нужна для сборки/миграции.

## 11. Практический вывод

Правильное решение для проекта выглядит так:

- **generic authoring API**
- **typed orchestration definitions**
- **generic compile/materialize/run pipeline**
- **одна узкая runtime strategy/factory точка для pattern-specific логики**

Это не противоречит текущей handoff orchestration как таковой. Противоречие только в том, что сейчас handoff-специфика протекла в compiler, UI, materializer и session service. Именно это и должно быть исправлено.
