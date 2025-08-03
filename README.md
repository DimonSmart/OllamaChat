# OllamaChat

OllamaChat is a sample chat client for local LLMs powered by
[Semantic Kernel](https://github.com/microsoft/semantic-kernel). It now
supports multi‑agent conversations through the `GroupChatOrchestration` API.

```csharp
var orchestrator = new GroupChatOrchestration(
    new RoundRobinGroupChatManager { MaximumInvocationCount = 2 },
    agent1, agent2);

await using var runtime = new InProcessRuntime();
await runtime.StartAsync();
var result = await orchestrator.InvokeAsync("Hello", runtime);
string final = await result.GetValueAsync();
```

Each agent is a `ChatCompletionAgent` with its own system prompt. The round‑robin
manager automatically rotates agents and stops after the specified number of
rounds.
