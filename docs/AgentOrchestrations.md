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

`WorkflowCodeTemplates.StarterTemplates` is the single source of truth for built-in starter workflows used by:

- the editor's `Load Starter Workflow` action
- fallback workflow seeding when no external `workflow_definitions.json` is present
- compiler smoke tests

Current built-in starters:

- `philosopher-battle-group-chat`
- `interview-coach-fixed-handoff`
- `research-brief-sequential`
- `proposal-panel-concurrent`

## Persistence And Seeding

- Saved workflows are stored through `WorkflowDefinitionService`.
- Workflow kinds are normalized on save/load through `WorkflowDefinitionKinds.Normalize`.
- If `WorkflowDefinitions:SeedFilePath` does not resolve to a valid seed file, `WorkflowDefinitionSeeder` seeds the built-in starter templates from `WorkflowCodeTemplates`.

## Notes

- `docs/AgentOrchestrationsPlan.md` is the migration/design spec.
- This file describes the current implementation shape after the generic orchestration migration.
