# Microsoft Agentic Framework Migration Plan (Parallel Coexistence, SK End-State Removal)

## Status

As of February 12, 2026, Semantic Kernel runtime paths were decommissioned and removed from active code paths.
The repository now runs on the Agentic runtime only.

This plan defines a strict parallel migration strategy:

- run two engines side-by-side during migration,
- keep them isolated,
- keep both user-facing chat scenarios (single-agent and multi-agent),
- remove Semantic Kernel only after Agentic reaches acceptance.

## 1. Clarified Intent

This is not a "fallback-only" strategy. It is a full parallel coexistence strategy with two active implementations:

- `Semantic Kernel engine` (existing implementation),
- `Agentic engine` (new implementation).

Both implementations are independently runnable and testable during migration.  
End-state: Semantic Kernel code path is decommissioned and removed from the repository once acceptance gates are met.

## 2. Migration Boundaries

Allowed reuse from current codebase:

- agent definitions and system prompts,
- server/model/user settings,
- HTTP client configuration and connectivity helpers,
- chat persistence and shared UI components where engine-agnostic.

Not allowed in new Agentic runtime paths:

- direct dependence on `Microsoft.SemanticKernel*` packages/types,
- reuse of SK orchestration/runtime primitives,
- reuse of SK plugin invocation pipeline as Agentic orchestration core.

## 3. Target Architecture

Introduce independent Agentic implementation paths:

- `ChatClient.Application/Services/Agentic/*` for contracts/options/models,
- `ChatClient.Api/Client/Services/Agentic/*` for runtime/orchestration/session services,
- optional `ChatClient.Infrastructure/.../Agentic*` for adapters if needed.

UI must expose separate entry points for both scenarios:

- single-agent: existing SK tab + new Agentic tab,
- multi-agent: existing SK tab + new Agentic tab.

Example routes:

- SK: `/chat`, `/multi-agent-chat`,
- Agentic: `/chat-agentic`, `/multi-agent-chat-agentic`.

## 4. Non-goals During Migration

- no big-bang rewrite,
- no shared mutable orchestration state between engines,
- no partial in-place mutation of SK runtime internals pretending to be Agentic.

## 5. Migration Principles

1. Isolation first: separate classes, DI graph, and runtime flow.
2. Parity by behavior: compare UX and outcomes, not SDK APIs.
3. Progressive rollout: enable by config per scenario.
4. Observability by default: identical metrics for both engines.
5. Explicit decommissioning: SK removal is a planned final phase, not ad-hoc cleanup.

## 6. Concrete Phases

### Phase A - Stabilize engine-neutral contracts

Create and use neutral interfaces for UI/controller orchestration, e.g.:

- `IChatEngineSessionService`
- `IChatEngineOrchestrator`
- `IChatEngineHistoryBuilder`
- `IChatEngineStreamingBridge`

Provide both implementations:

- `SemanticKernelChatEngine*` adapter layer,
- `AgenticChatEngine*` implementation.

Exit criteria:

- UI and orchestration entry points depend on engine-neutral contracts,
- SK-specific types are not required by shared UI orchestration flow.

### Phase B - Agentic single-agent chat path

Build complete Agentic single-agent flow:

- request/response with streaming,
- cancellation propagation,
- model selection via existing server settings,
- message lifecycle parity with current UX.

Expose via dedicated Agentic single-agent route/tab.

### Phase C - Agentic multi-agent chat path

Build complete Agentic multi-agent flow:

- round-robin baseline,
- strategy controls equivalent to existing UX,
- stop conditions and max-invocation safeguards.

Expose via dedicated Agentic multi-agent route/tab.

### Phase D - Tooling/function parity in Agentic

Implement Agentic-native tool execution with:

- strict argument schema validation,
- invocation timeout and retry policy,
- deterministic tool call logging and diagnostics.

### Phase E - RAG/memory parity in Agentic

Implement explicit retrieval behavior for Agentic runtime:

- reuse indexing/vector services where practical,
- keep retrieval pipeline runtime-agnostic,
- preserve source injection behavior visible to users.

### Phase F - Side-by-side validation and rollout

Run controlled side-by-side comparison for both scenarios:

- same prompts and model where possible,
- compare quality, tool success rate, cancellation reliability, latency, token/cost,
- promote Agentic scenario-by-scenario.

### Phase G - Agentic default by scenario

Switch defaults to Agentic where acceptance gates are satisfied, while SK path still exists for controlled coexistence.

### Phase H - Semantic Kernel decommission and removal

When both single-agent and multi-agent Agentic paths pass acceptance:

- remove SK runtime/service registrations,
- remove SK-based UI paths/tabs,
- remove SK package references,
- delete obsolete SK adapters/tests/docs,
- finalize migration documentation.

## 7. Acceptance Gates Before SK Removal

All of the following must be true:

- no blocking UX regressions in single-agent and multi-agent scenarios,
- tool call success rate at or above SK baseline,
- cancellation behavior reliable under load,
- latency/cost within agreed budget,
- production logs support root-cause analysis,
- parity tests for session lifecycle and streaming pass for both scenarios.

## 8. Repository Work Items (Updated)

1. Keep `docs/agentic-migration-plan.md` and ADR in sync with this strategy.
2. Complete engine-neutral contracts usage in UI/session orchestration.
3. Keep Agentic namespaces free from `Microsoft.SemanticKernel*` dependencies.
4. Add/complete dedicated Agentic routes for single-agent and multi-agent chat.
5. Implement Agentic tool policy (validation + timeout + retry + diagnostics).
6. Implement/verify Agentic RAG parity behavior for ongoing conversations.
7. Add automated parity tests for single-agent and multi-agent lifecycle/streaming.
8. Define explicit SK removal checklist and execute only after acceptance gates.

## 9. Why This Fits the Project

The repository already has:

- modular DI registration,
- service-oriented structure,
- reusable server/model/settings services,
- chat history and streaming abstractions.

This enables safe parallel development and controlled replacement, with a clear end-state where Agentic fully replaces SK.
