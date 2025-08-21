# Multi-Agent Chat

OllamaChat uses Semantic Kernel's `GroupChatOrchestration` to coordinate
selected `ChatCompletionAgent` instances. A `RoundRobinGroupChatManager`
cycles through agents; increase `MaximumInvocationCount` to let them
auto‑continue without extra user messages.

The orchestrator's `ResponseCallback` writes messages directly to the chat
history and the UI displays agent names from `ChatCompletionAgent.Name`.

To keep the last speaker consistent, a `ForceLastUserReducer` rewrites the
final agent reply as a user message (`AuthorName="user"`).

```csharp
var orchestrator = new GroupChatOrchestration(
    new RoundRobinGroupChatManager { MaximumInvocationCount = 2 },
    agents);
```

