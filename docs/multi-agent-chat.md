# Multi-Agent Chat

OllamaChat now relies on Semantic Kernel's built-in **GroupChatOrchestration** for
multi-agent conversations. Every selected system prompt becomes a
`ChatCompletionAgent`, and a `RoundRobinGroupChatManager` rotates agents in turn.

```csharp
var ruToEn = new ChatCompletionAgent
{
    Name = "ru_to_en",
    Description = "Russian to English translator",
    Kernel = kernel,
    Instructions = "If last message is Russian, translate it to English."
};

var enToEs = new ChatCompletionAgent
{
    Name = "en_to_es",
    Description = "English to Spanish translator",
    Kernel = kernel,
    Instructions = "If last message is English, translate it to Spanish."
};

var orchestrator = new GroupChatOrchestration(
    new RoundRobinGroupChatManager { MaximumInvocationCount = 2 },
    ruToEn, enToEs);

await using var runtime = new InProcessRuntime();
await runtime.StartAsync();
var result = await orchestrator.InvokeAsync("Привет", runtime);
string final = await result.GetValueAsync();
```

`MaximumInvocationCount` controls how many times agents are invoked. Set it to a
value greater than `1` to let agents auto‑continue without additional user
messages.

The chat UI records agent names from `ChatCompletionAgent.Name` so each message
shows the speaker. No custom coordinator or manual history management is
required—the orchestrator's `ResponseCallback` adds messages to the chat
history directly.

