# Multi-Agent Chat

OllamaChat uses Semantic Kernel's `GroupChatOrchestration` to coordinate
selected `ChatCompletionAgent` instances. The `GroupChatManagerFactory`
creates various group chat managers such as `BridgingRoundRobinManager`
that cycle through agents. Increase `MaximumInvocationCount` to let them
auto‑continue without extra user messages.

The orchestrator's `ResponseCallback` writes messages directly to the chat
history and the UI displays agent names from `ChatCompletionAgent.Name`.

To keep the last speaker consistent, an `AppForceLastUserReducer` rewrites the
final agent reply as a user message (`AuthorName="user"`).

```csharp
var factory = new GroupChatManagerFactory();
var manager = factory.Create("RoundRobin", new RoundRobinChatStrategyOptions { Rounds = 2 });
var orchestrator = new GroupChatOrchestration(manager, agents);
```

