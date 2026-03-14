# Planning Runtime

`OllamaChat` includes a dedicated planning runtime for multi-step task execution. It is exposed in the UI on the `Planning Chat` page at `/chat-planning` and is implemented under `ChatClient.Api/PlanningRuntime/`.

## What It Does

The planning runtime takes a user request and runs a bounded plan-execute-verify-replan loop:

1. Build a structured plan with `LlmPlanner`.
2. Execute the plan step by step with `PlanExecutor`.
3. Verify the result with `GoalVerifier`.
4. If needed, repair the working plan with `LlmReplanner`.
5. Optionally verify the final answer with `LlmFinalAnswerVerifier`.

The runtime is intended for tasks where the model should first decide which external data to fetch, then transform that data through explicit intermediate steps before returning a final answer.

## App Integration

The runtime is integrated into the main ASP.NET Core app rather than living as a separate demo project.

- UI route: `/chat-planning`
- Page: `ChatClient.Api/Client/Pages/ChatPlanning.razor`
- State/service entry point: `IPlanningSessionService` / `PlanningSessionService`
- DI registration: `ChatClient.Api/ServiceCollectionExtensions.cs`

`PlanningSessionService` is responsible for:

- validating the start request,
- resolving the selected MCP-backed tools from the shared app tool catalog,
- building the runtime graph for the selected model and enabled tools,
- running the orchestration loop in the background,
- projecting live state into `PlanningSessionState`,
- publishing execution events and log lines to the UI,
- supporting cancel and reset.

The page shows:

- the current plan graph,
- the selected step details,
- replanning and diagnostic logs,
- the final `ResultEnvelope<JsonElement?>`.

## Runtime Flow

### Start

`PlanningSessionService.StartAsync` requires:

- a selected `ServerModel`,
- a non-empty user query,
- at least one enabled planning tool.

When a run starts, the service resets previous state, stores the request, exposes the available tools in UI state, and launches execution with a fresh cancellation token.

The available tool list is loaded dynamically from the same MCP-derived catalog that the regular chat runtime uses. The user explicitly selects which MCP tools the planner may call for a given run.

### Orchestration

`PlanningOrchestrator` performs up to `3` attempts by default:

1. Create the initial plan.
2. Execute it.
3. Check whether the goal was achieved.
4. If not achieved and replanning is available, create a repaired plan and try again.

If verification concludes that the final answer is still incomplete, the orchestrator converts that into a replan request instead of silently accepting the output.

### Completion and Cancellation

- Successful completion stores the final envelope in session state.
- Cancellation clears the final result and logs `[planning] canceled`.
- Unexpected exceptions are converted to `planning_failed`.

## Plan Contract

The planner returns `ResultEnvelope<PlanDefinition>`. The plan itself has:

```json
{
  "goal": "string",
  "steps": [
    {
      "id": "string",
      "tool": "tool-name",
      "in": {},
      "s": "todo",
      "res": null,
      "err": null
    }
  ]
}
```

A step must define exactly one of:

- `tool`: execute a registered tool
- `llm`: execute an ad-hoc LLM reasoning step

For LLM steps, `systemPrompt` and `userPrompt` are required. `out` may be `json` or `string`.

LLM steps also declare an explicit output contract:

```json
"out": {
  "format": "json|string",
  "aggregate": "single|collect|flatten",
  "schema": { "type": "object|array|string|number|boolean|null" }
}
```

Output contract semantics:

- `format` tells the runner whether the step returns plain text or JSON
- `aggregate=single` means one call produces one final step result
- `aggregate=collect` means a mapped step returns one item per call and the executor collects those items into an array
- `aggregate=flatten` means a mapped step returns an array per call and the executor flattens all call results into one array
- `schema` describes the expected per-call JSON shape for LLM outputs and is also used to validate the final aggregated result
- tool steps may omit `out`; in that case the runtime derives the contract from the tool output schema

Current step statuses:

- `todo`
- `running`
- `done`
- `fail`
- `skip`

## Input Bindings and Fan-Out

Step inputs live under `in`. Each input may be:

- a literal JSON value, passed as-is
- a binding object:

```json
{
  "from": "$search.results",
  "mode": "map"
}
```

Supported reference forms inside `from`:

- `$stepId`
- `$stepId.field`
- `$stepId.field[]`
- `$stepId.field[].nested`
- `$stepId.field[n]`
- `$stepId.field[n].nested`

Important behavior:

- references may only target earlier steps,
- raw strings like `"$search.results"` are rejected inside `in`; use a binding object instead,
- LLM prompts must not embed `$stepId` references directly,
- `mode=value` is the default and resolves one value for one call,
- `mode=map` means the step runs once per array item,
- if multiple inputs use `mode=map`, they are zipped by index and must resolve to the same length,
- literal inputs are broadcast into every mapped call.

This keeps fan-out explicit in the plan instead of relying on executor heuristics.

## Validation Rules

`PlanValidator` enforces the plan contract before execution and after replanning. Key constraints:

- `goal` is required,
- `steps` must be non-empty,
- step ids must be unique,
- each step must have exactly one of `tool` or `llm`,
- every step must declare `in`,
- tool steps may only pass inputs declared in the MCP tool input schema,
- LLM steps must provide `systemPrompt` and `userPrompt`,
- prompts may not contain embedded step refs like `$searchPages[]`,
- prompts may not use template placeholders like `{{var}}`.

## MCP Tool Integration

The planner no longer uses a separate `ITool` abstraction. It consumes the shared `AppToolDescriptor` catalog, which is built from connected MCP servers and built-in MCP servers.

That means:

- regular chat and planning use the same discovered tool list,
- planner prompts reuse MCP `inputSchema`, `description`, and `outputSchema`,
- planner execution invokes the same MCP-backed tool delegates as regular chat,
- MCP elicitation dialogs are supported inside the planning UI through the dedicated `Planning` interaction scope.

## Built-In Web MCP Tools

The app now exposes web search/download as a built-in MCP server, and those tools automatically appear in both the regular chat tool catalog and the planning UI.

### `search`

- Implementation: built-in MCP server `BuiltInWebMcpServerTools` backed by `BuiltInWebToolLogic`
- Purpose: search the web and return structured candidate page objects
- Input: `query`, optional `limit`
- Default/max limit: `4` / `6`
- Current implementation: fetches Brave Search HTML, parses structured Brave result cards, filters Brave-owned hosts, and returns an object with `query` and `results`

### `download`

- Implementation: built-in MCP server `BuiltInWebMcpServerTools` backed by `BuiltInWebToolLogic`
- Purpose: download one page and return the original page description object enriched with normalized `content`
- Input: either a full search-result object under `page`, or an absolute HTTP/HTTPS `url`
- Current implementation: fetches HTML, strips script/style/noscript/svg nodes with HtmlAgilityPack, normalizes whitespace, truncates text to `12000` characters, and returns a structured object with `url`, `title`, `content`, and preserved search metadata when available

Both tools publish MCP output schemas and structured content, so the planner can reuse MCP metadata directly instead of maintaining a separate planner-specific schema model.

## Verification

The runtime has two verification layers.

### Step-level verification

`StepOutputVerifier` checks for null, empty, or structurally empty outputs. `StepOutputContractValidator` then validates successful step outputs against the declared `out` contract, both per call and after any `collect`/`flatten` aggregation. If validation fails, the executor marks the step as failed with `output_contract_failed` and includes structured issue details such as `expected object, got array` or missing required fields.

### Goal-level verification

`GoalVerifier` decides whether to:

- finish with `Done`,
- request `Replan`,
- or ask the user for clarification with `AskUser`.

`AskUser` is triggered when the final step returns an object with `needUserInput=true`. Empty final objects, arrays, or strings are treated as failures that require replanning.

### Final-answer verification

`LlmFinalAnswerVerifier` performs a final semantic check on the last step result. If the result does not actually answer the original user request, the runtime feeds that back into replanning instead of accepting a superficially successful plan.

## Replanning

`LlmReplanner` does not replace the whole plan blindly. It edits the existing working plan through a constrained action protocol backed by `PlanEditingSession`.

Current plan-editing actions:

- `plan.readStep`
- `plan.replaceStep`
- `plan.addSteps`
- `plan.resetFrom`
- `runtime.readFailedTrace`

The replanner receives:

- the original user query,
- the current working plan,
- execution summaries,
- failed trace hints,
- the current goal verdict,
- the last round of action results.

This makes replanning incremental and lets successful upstream steps stay reusable.

## Events and UI Projection

Runtime execution emits structured `PlanRunEvent` records. Examples include:

- planning attempt started,
- plan created,
- step started,
- step call started/completed,
- step completed,
- goal verified,
- final answer verified,
- replan started,
- replan round completed,
- replan applied,
- run completed.

`PlanningSessionService` listens to those events and projects them into UI state:

- `CurrentPlan`
- `ActiveStepId`
- `Events`
- `LogLines`
- `FinalResult`

That projected state is what the `/chat-planning` page renders.

## Tests

The current planning runtime is covered by focused tests in `ChatClient.Tests`:

- `PlanningRuntimeContractsTests`
- `PlanningPipelineIntegrationTests`

These tests cover contract validation, session-state behavior, and end-to-end pipeline execution for the planning loop.
