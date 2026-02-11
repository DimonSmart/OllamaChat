# ADR 0001: Parallel Agentic Engine Introduction

- Date: 2026-02-11
- Status: Accepted

## Context

The repository currently uses a Semantic Kernel runtime as the primary chat engine for single-agent and multi-agent scenarios.
A direct in-place migration to Microsoft Agent Framework would create high rollout risk, since chat UX, streaming behavior, model/tool wiring, and cancellation semantics are tightly integrated.

## Decision

Introduce a second, isolated engine path named `Agentic` while preserving the existing Semantic Kernel runtime.

The migration strategy is:

1. Keep the current SK runtime untouched.
2. Add engine-neutral contracts in `ChatClient.Application/Services/Agentic`.
3. Add new `Agentic` services in `ChatClient.Api/Client/Services/Agentic`.
4. Add a dedicated UI route `/chat-agentic` with independent scoped state.
5. Add feature flag `ChatEngine:Mode = SemanticKernel | Agentic | Dual` for progressive rollout.

## Consequences

### Positive

- Side-by-side comparison is possible without regressions in the existing chat path.
- Rollout can be controlled with configuration and reverted quickly.
- The `Agentic` implementation can evolve independently toward parity.

### Negative

- Temporary duplication of some service/view-model glue code.
- Increased DI and maintenance complexity during migration period.

## Follow-up

- Expand parity tests to include cancellation, streaming ordering, and tool-call reliability.
- Add explicit performance and cost comparison metrics for both engines.
- Promote `Agentic` by scenario only after acceptance gates are met.
