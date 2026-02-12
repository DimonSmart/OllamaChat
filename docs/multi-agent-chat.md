# Multi-Agent Chat in the Legacy Semantic Kernel Engine (Archived)

This document is historical context from the pre-Agentic runtime implementation.
The active runtime no longer depends on Semantic Kernel.

## Execution flow

1. `AppChatService` starts a chat session and validates selected agents.
2. A Semantic Kernel `InProcessRuntime` is created for orchestration.
3. Agent instances are built from configured agent descriptions.
4. Input is transformed into `ChatHistory` using `IAppChatHistoryBuilder`.
5. `GroupChatOrchestration` executes with a selected group manager strategy.
6. Streaming output is appended to UI messages and finalized when complete.

## Group orchestration

The app uses Semantic Kernel group chat orchestration (`GroupChatOrchestration`) with manager strategies created through `IGroupChatManagerFactory`.

Examples of behavior implemented in the current engine:

- strategy-based round-robin agent turn selection,
- runtime invocation count resets,
- stop-phrase evaluation,
- temporary UI placeholder messages while waiting for first tokens,
- cancellation support for active streams.

## Chat history shaping

Before orchestration execution, messages are filtered and converted to Semantic Kernel `ChatHistory`.

The pipeline includes:

- dropping transient streaming placeholders,
- preserving uploaded file references,
- optional insertion of tool/RAG context messages,
- chat history reducers for output normalization.

A dedicated reducer rewrites the last message role in specific scenarios to keep UI behavior consistent with the app's interaction model.

## Why this matters for migration

This implementation is tightly coupled to Semantic Kernel orchestration/runtime types. Migrating to Microsoft Agentic Framework should preserve:

- the same UI streaming behavior,
- the same message lifecycle and cancellation semantics,
- parity for group strategy behavior,
- compatibility with current server/model configuration and RAG flow.

For the migration plan, see `docs/agentic-migration-plan.md`.
