# Steering technical gate — MAF 1.27.0

**Date:** 2026-11-10  
**Specification:** `OllamaChat-Steering-TZ-reviewed-v3-2026-11-10`, sections 5–6, 8–10, 12–13.  
**Status:** **BLOCKED**. Steering is not enabled, and no UI, session, transcript, or persistence behavior has been changed.

## What was verified

The project uses `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Harness` 1.27.0. The native Harness pipeline is:

```
FunctionInvokingChatClient
  -> MessageInjectingChatClient
  -> PerServiceCallChatHistoryPersistingChatClient
  -> optional Compaction
  -> root IChatClient
```

`MessageInjectingChatClient` is retrievable via `HarnessAgent.GetService<MessageInjectingChatClient>()`. It supports `EnqueueMessagesAsync` and `GetPendingMessagesAsync`, but does **not** expose a public dequeue, removal, or clear operation.

Its streaming implementation drains the queue when a service request begins, and after a response without actionable tool calls checks `IsQueueEmptyAsync`. When the check finds pending messages, it loops and issues an additional underlying LLM request **within the same Harness run**. This supports approved **variant B**. The non-streaming implementation follows the same pattern.

The queue is per-`AgentSession`, is stored in its `StateBag`, and is used across successive runs of the same session. The session bag is serializable.

## Gate failure: admission vs. terminal native queue check

The injector has a gap after its last empty-queue check and before `RunStreamingAsync` finishes unwinding into OllamaChat's `RunDirectAsync`. In the current service, `IsAnswering` is not set to false until the `finally` block *after* iteration and checkpoint work.

A future Steering implementation that validates `IsAnswering`/`RunId` in the service and then calls `EnqueueMessagesAsync` can therefore produce this sequence:

1. The native injector completes its last queue check and decides to finish the run.
2. The service still considers that run active and accepts/enqueues a Steering instruction.
3. There is no further model call in that run. The instruction must be marked **Not delivered**.
4. The queued message still exists in `AgentSession.StateBag`.
5. A later, ordinary user turn on the same session causes the injector to drain that old instruction into the **new run**, violating the run boundary and contradicting the transcript status.

The coordinator's `_lifecycleLock` / `_runSetupGate` cannot close this gap: the injector uses its own private per-session semaphore, and its terminal queue check is not exposed as an atomic admission protocol.

`GetPendingMessagesAsync` can observe leftovers but cannot remove them. Directly modifying the Framework-owned `MessageInjectingChatClient.PendingInjectedMessages` state key (even through public `AgentSession.StateBag.TryRemoveValue`) is **expressly forbidden by the specification**. Discarding the session also discards other native Harness state and breaks continuation/Saved Chats; this is not an acceptable silent workaround.

## Deterministic characterization tests

`ChatClient.Tests/HarnessSteeringInjectionGateTests.cs` uses real `HarnessAgent` 1.27.0 and a controlled fake `IChatClient`, without real model calls or network:

- `LateMessage_DuringActiveModelCall_TriggersAdditionalModelCallInSameRun`: confirms one additional model request sees the injected `ChatRole.User` content and the native queue is drained.
- `UndeliveredNativeMessage_RemainsInSessionAndLeaksIntoFollowingRun`: confirms that a message enqueued after a completed native run is consumed by the next run on the same session.
- `UndeliveredNativeMessage_SurvivesHarnessSessionSerialization`: probes whether the pending queue also survives serialization/restore.

The second test characterizes the native API behavior and models the proven gap. It does **not** demonstrate that OllamaChat currently accepts Steering: the product feature does not exist yet.

All three characterization tests, the solution build, and the existing regression suite passed on [GitHub Actions](https://github.com/DimonSmart/OllamaChat/actions/runs/42815413817). The separate [scheduled tasks validation](https://github.com/DimonSmart/OllamaChat/actions/runs/42815413856) also passed.

## Alternatives considered

1. **Plain native enqueue:** does not pass the terminal race / next-run isolation gate. Cannot ship.
2. **Use `AIContextProvider` or root-only `DelegatingChatClient` in place of native injection:** could add messages at a model boundary but does not automatically provide variant B's extra LLM call after a final response. It also requires proof of native history persistence, compaction ordering, tool call/result ordering, and restore.
3. **Hybrid root-only middleware, application admission gate, and native late continuation:** a possible follow-up investigation, **not** a verified implementation. It would need deterministic proof that only safe in-flight calls enqueue native messages, that all other admissions are delivered/persisted at a real root request boundary, and that no stale queue survives any terminal path. It must not introduce a custom agent loop or two independent delivery queues.
4. **Extend/upstream Framework API:** a supported `TryEnqueueForActiveRunAsync` or per-message `RemovePendingMessagesAsync` with a terminal handshake could make the native path safe. The exact contract and Framework version require separate verification.

## Decision

Stop before implementation, per specification §6.2 / §13. No partial Steering UI or service should be introduced with false `Delivered` statuses, pending-message leaks, or unsafe native-state edits. The agreed variant B remains unchanged.

Once a path passes deterministic tests for atomic admission, late delivery, native history/checkpoint/restore, cancel/approval, and run isolation, continue with the implementation and durable product intent changes. Until then, OllamaChat's direct-agent behavior remains unchanged.

## Reference source (exact framework tag)

- [HarnessAgent.cs](https://github.com/microsoft/agent-framework/blob/dotnet-1.27.0/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgent.cs)
- [MessageInjectingChatClient.cs](https://github.com/microsoft/agent-framework/blob/dotnet-1.27.0/dotnet/src/Microsoft.Agents.AI/ChatClient/MessageInjectingChatClient.cs)
- [PerServiceCallChatHistoryPersistingChatClient.cs](https://github.com/microsoft/agent-framework/blob/dotnet-1.27.0/dotnet/src/Microsoft.Agents.AI/ChatClient/PerServiceCallChatHistoryPersistingChatClient.cs)
- [AgentSessionStateBag.cs](https://github.com/microsoft/agent-framework/blob/dotnet-1.27.0/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSessionStateBag.cs)
