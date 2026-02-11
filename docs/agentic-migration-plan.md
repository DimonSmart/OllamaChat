# Microsoft Agentic Framework Migration Plan (Parallel Engine Strategy)

This plan aligns with the current codebase and with the target vision: **do not rewrite everything in place**. Instead, implement a **new independent engine** in parallel, expose it in a separate UI path/tab, and migrate incrementally with measurable regression control.

## 1. Current baseline in this repository

Today the app uses Semantic Kernel as the active engine for:

- single-agent chat,
- multi-agent orchestration (`GroupChatOrchestration` + group managers),
- streaming updates and cancellation,
- tool/function integration,
- RAG context enrichment,
- Ollama and OpenAI-compatible model endpoints.

The current architecture already has service boundaries that can be used for side-by-side evolution.

## 2. Target architecture

Introduce a second chat engine called **Agentic Engine** with fully separate classes/files:

- `ChatClient.Api/Client/Services/Agentic/*` – new engine services,
- `ChatClient.Application/Services/Agentic/*` – interfaces/contracts,
- optional `ChatClient.Infrastructure/.../Agentic*` for persistence adapters,
- dedicated UI route/page/tab (for example: `"/chat-agentic"`).

The existing Semantic Kernel engine remains unchanged and operational during rollout.

## 3. Non-goals (important)

- No big-bang rewrite of current SK services.
- No immediate deletion of SK-based runtime.
- No shared mutable orchestration state between engines.

## 4. Migration principles

1. **Isolation first**: independent classes, DI registrations, and orchestration runtime.
2. **Parity by contract**: match user-visible behavior before optimization.
3. **Feature flags**: enable progressive rollout by configuration.
4. **Observability by default**: compare both engines with the same metrics.

## 5. Concrete phased plan

## Phase A — Stabilize contracts (short)

Create neutral interfaces used by UI and controllers, for example:

- `IChatEngineSessionService`
- `IChatEngineOrchestrator`
- `IChatEngineHistoryBuilder`
- `IChatEngineStreamingBridge`

Provide two implementations:

- `SemanticKernelChatEngine*` (adapter over current code)
- `AgenticChatEngine*` (new code)

Result: UI no longer depends on SK-specific types directly.

## Phase B — Build Agentic Engine skeleton

Implement minimal Agentic runtime with:

- one-agent request/response flow,
- streaming text output to existing UI message model,
- cancellation propagation,
- model selection from existing server config entities.

Do not implement multi-agent yet.

## Phase C — Add dedicated UI tab/route

Add a second chat entry point in UI:

- existing tab/page = current SK engine,
- new tab/page = Agentic engine.

Keep independent session state so users can compare behavior quickly and safely.

## Phase D — Tooling/function parity

Migrate current tool/function calls into Agentic-compatible tool registration.

Requirements:

- strict argument schema validation,
- invocation timeout and retry policy,
- deterministic tool logging for diagnostics.

## Phase E — RAG and memory parity

Preserve current RAG behavior through an explicit retrieval tool in Agentic engine:

- reuse existing file indexing and vector search services where possible,
- keep retrieval pipeline independent from orchestration implementation,
- maintain source injection behavior into chat history equivalent to current UX.

## Phase F — Multi-agent orchestration parity

Implement multi-agent flows in Agentic engine:

- start with simple round-robin parity,
- add strategy controls equivalent to current managers,
- preserve stop conditions and max-invocation safeguards.

## Phase G — Validation and progressive rollout

Use side-by-side evaluation:

- same prompts, same model where possible,
- compare response quality, tool success rate, latency, and token usage,
- promote Agentic engine scenario-by-scenario.

Keep SK engine as fallback until acceptance targets are stable.

## 6. Feature compatibility matrix (what may differ)

Potential differences to plan for explicitly:

- planner/orchestration APIs are not 1:1,
- middleware/filter extension points differ,
- prompt templating behavior may differ,
- streaming event granularity may differ,
- memory abstractions are often implemented differently.

For each area, define parity tests at the **behavior level**, not SDK API level.

## 7. Suggested acceptance gates

Promote a scenario to Agentic-by-default only when all are true:

- no blocking regressions in UX,
- tool call success rate is at or above SK baseline,
- cancellation works reliably,
- latency/cost are within agreed budget,
- production logs support root-cause analysis.

## 8. Recommended repository work items

1. Add `docs/agentic-migration-plan.md` (this file).
2. Add architecture decision record: `docs/adr/0001-parallel-agentic-engine.md`.
3. Introduce engine-neutral interfaces in `ChatClient.Application`.
4. Create `Agentic` namespaces/folders in API/Application projects.
5. Add feature flag: `ChatEngine:Mode = SemanticKernel | Agentic | Dual`.
6. Add dedicated UI route/tab for Agentic sessions.
7. Add automated parity tests for session lifecycle and streaming behavior.

## 9. Why this strategy fits this project

The project already has:

- modular DI registration,
- clear service-oriented organization,
- existing server/model configuration services,
- chat history and streaming abstractions.

That makes side-by-side engine development practical and lower-risk than in-place replacement.
