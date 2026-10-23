# LLM cassette tests (test-only)

The cassette intercepts `Microsoft.Extensions.AI.IChatClient`; the agent, Harness, and tools are **not** mocked by this adapter. One logical client per cassette, sequential exchanges.

- **Replay** is the default: a committed JSON fixture is required. No model or network access is used.
- **Record** is explicit: set `OLLAMACHAT_LLM_CASSETTE_RECORD=1` when running an integration test designed for recording. It must create a real client with the existing `LlmChatClientFactory` and call `CompleteAsync` only after every assertion succeeds.
- Tests that use synthetic fake clients for adapter unit coverage generate temporary cassette files and do not require network access.

Run the adapter and real-Harness integration tests (xUnit v3):

```sh
dotnet test ChatClient.Tests/ChatClient.Tests.csproj --filter-class "ChatClient.Tests.LlmCassetteTests"
dotnet test ChatClient.Tests/ChatClient.Tests.csproj --filter-class "ChatClient.Tests.LlmCassetteHarnessTests"
```

The cassette schema is `schemaVersion: 1` with an ordered array of `exchanges` (method, request, response). Streaming updates stay separate and in order. Messages, function calls/results, relevant model options and function declarations are compared structurally. Object key ordering does not matter; array ordering, types and missing properties do. A mismatch reports the exchange number and first differing path. Unsupported content fails rather than being silently lost.

Provide `workspaceRoot` to `LlmCassette` in both modes to replace workspace path occurrences with `{{WORKSPACE_ROOT}}` and restore paths in replayed tool arguments. Make nondeterministic test inputs deterministic rather than introducing additional masks.

`CompleteAsync` checks exchange exhaustion and atomically publishes Record output. It does not run from `Dispose`, and an exception, interrupted stream or cancellation prevents publishing. The same file must never be recorded concurrently. Review the JSON diff before committing a fixture; never record real user files or secrets.

**Integration coverage:** `LlmCassetteHarnessTests` records a deterministic scripted LLM response into a temporary cassette, then runs a **real Harness and real read-only File Access** in Replay. A changed README triggers a mismatch. This works offline on CI, but the generated cassette is intentionally ephemeral, not a committed real-LLM recording.

**Remaining acceptance item:** The specified headless Saved Agent + Saved Workspace integration using `IAgentRunner` with a committed cassette **recorded from an actual function-calling model** has not yet been implemented. It requires a local model and reviewed Record output; the synthetic Harness integration does not replace that requirement.
