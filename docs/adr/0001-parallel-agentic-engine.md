# ADR 0001: Parallel Agentic Engine Coexistence with SK End-State Removal

- Date: 2026-02-11
- Status: Completed (2026-02-12)

> Historical ADR: the coexistence phase described below is finished.
> The repository now runs Agentic-only runtime paths and SK has been removed from active code.

## Context

The repository currently uses a Semantic Kernel runtime as the primary chat engine for single-agent and multi-agent scenarios.
A direct in-place migration to Microsoft Agent Framework would create high rollout risk, since chat UX, streaming behavior, model/tool wiring, and cancellation semantics are tightly integrated.

## Decision

Introduce a second, isolated engine path named `Agentic` while preserving the existing Semantic Kernel runtime during migration.

The migration strategy is:

1. Run SK and Agentic as full parallel implementations during migration (single-agent and multi-agent scenarios).
2. Add engine-neutral contracts in `ChatClient.Application/Services/Agentic`.
3. Add new `Agentic` services in `ChatClient.Api/Client/Services/Agentic`.
4. Add dedicated Agentic UI routes for both scenarios with independent scoped state.
5. Add feature flag `ChatEngine:Mode = SemanticKernel | Agentic | Dual` for progressive rollout.
6. Decommission and remove SK runtime and package dependencies only after Agentic acceptance gates are met.

## Consequences

### Positive

- Side-by-side comparison is possible without regressions in the existing chat path.
- Rollout can be controlled with configuration and reverted quickly.
- The `Agentic` implementation can evolve independently toward parity.
- End-state is explicit: SK is not a permanent fallback.

### Negative

- Temporary duplication of some service/view-model glue code.
- Increased DI and maintenance complexity during migration period.

## Follow-up

- Expand parity tests to include cancellation, streaming ordering, and tool-call reliability.
- Add explicit performance and cost comparison metrics for both engines.
- Promote `Agentic` by scenario only after acceptance gates are met.
- Add and execute an SK removal checklist once both Agentic scenarios are accepted.
