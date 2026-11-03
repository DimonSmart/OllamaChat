# LLM Context Timeline

The **Context** tab in the existing Trace drawer shows individual model-client calls for a chat run. It is intended for diagnosing Harness requests, not for editing conversation state.

## Capture and accuracy

- **Metadata** (default) stores invocation metadata, content kinds, category totals, tool names and provider usage, but not prompt text, tool arguments/results, schemas or their fingerprints.
- **Full context** is explicit and prospective. It may contain private prompts, secrets and tool outputs. It is stored only in memory and can be removed with **Clear captured content**. A new/reset conversation clears the store and returns capture to Metadata.
- **Off** stops capturing future calls without affecting Trace.
- The capture boundary is `Microsoft.Extensions.AI.IChatClient`. The OpenAI-compatible adapter may serialize or transform the request after this boundary. The inspector does **not** claim to show wire-exact HTTP JSON.
- Segment sizes are **rough character-count / 4 estimates**, not model-specific tokenizer results. Provider-reported input/output/cached/reasoning counts describe a **whole invocation** and are never distributed artificially over segments.
- Unknown-size typed contents (image, audio, binary or opaque content) are recorded as Unknown/Partial; binary data is not embedded in the inspector. Tool definitions come only from the observed `ChatOptions.Tools`.
- Categories are mutually exclusive. A tool result in conversation history is classified as **ToolResults**, not counted a second time in Conversation.
- A smaller next input is not proof of compaction. The inspector does not infer a compaction event merely from bar sizes. Confirmed compaction event markers remain dependent on an observable Harness signal.
- Linkage to a Trace chat span requires the exact trace/span ID to be present in the recorded run; otherwise the link remains Pending/Unavailable and navigation is disabled.

## Operational behavior

The store is scoped to the Blazor circuit. Defaults: 20 runs, 250 calls per run, 2000 segments per call, 64 MiB total retained content, 256 KiB maximum per blob. Duplicate full-content blobs are shared using SHA-256 and reference counts. Hitting a limit marks a truncated snapshot instead of failing the model request.

Background agents and separately instantiated summarizer clients are attributed to the agent that created them. Summarization on the primary model uses a non-owning purpose adapter; no tool-calling or history logic is replaced. Workflows using the same factory are captured; routes without a direct Trace run show their calls under **Unlinked calls**. The tool does not reconstruct old content from saved chats.

## Testing

The `ContextCaptureChatClientTests` suite uses deterministic `IChatClient` fakes to check streaming and non-streaming transparency, prospective privacy, token usage, concurrent scope isolation, truncation and failure. Use:

```sh
dotnet build OllamaChat.sln
dotnet test ChatClient.Tests/ChatClient.Tests.csproj --filter FullyQualifiedName~ContextCaptureChatClientTests
```

No external Ollama server is required for these tests.

## Current limitations

HTTP transport interception, provider-specific tokenizer accuracy, automatic context attribution to RAG/memory origins without metadata, and A/B invocation comparison are deliberately not included. Some hosted workflow routes do not expose a directly correlatable Trace run. Screenshots and real-model smoke tests require a running OllamaChat session and were not captured by the headless unit-test runner.
