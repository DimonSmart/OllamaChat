# Agent Orchestrations

Current workflow authoring and execution is centered around a single generic orchestration pipeline:

1. Author a workflow with `WorkflowDefinitionBuilder`.
2. Compile it to `IOrchestrationWorkflowDefinition`.
3. Materialize agent drafts and saved-agent references.
4. Build the runtime `Workflow` through `IOrchestrationRuntimeWorkflowBuilder`.
5. Run it through `IOrchestrationWorkflowSessionService`.

## Supported Kinds

- `handoff`
- `group-chat`
- `sequential`
- `concurrent`

Pattern-specific runtime code is localized under `ChatClient.Api/AgentWorkflows/Runtime`.

## Starter Templates

Seeded workflow source files under `ChatClient.Api/Data/workflows/*.workflow.csx` are the single source of truth for built-in starter workflows used by:

- `WorkflowDefinitionSeeder` during first-run seeding
- the saved workflow catalog surfaced by the `Load Workflow` dialog and the workflow definitions management page
- compiler smoke tests

Current seeded starters:

- `philosopher-battle-group-chat`
- `interview-coach-fixed-handoff`
- `research-brief-sequential`
- `proposal-panel-concurrent`

## Persistence And Seeding

- Saved workflows are stored through `WorkflowDefinitionService`.
- Workflow kinds are normalized on save/load through `WorkflowDefinitionKinds.Normalize`.
- `WorkflowDefinitionSeeder` loads starter workflow source files from `WorkflowDefinitions:SeedDirectoryPath`.

## Notes

- `docs/AgentOrchestrationsPlan.md` is the migration/design spec.
- This file describes the current implementation shape after the generic orchestration migration.
