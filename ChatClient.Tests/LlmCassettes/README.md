# LLM cassette tests (test-only)

The cassette intercepts `Microsoft.Extensions.AI.IChatClient`; the agent, Harness, and tools are **not** mocked by this adapter. One logical client per cassette, sequential exchanges.

- **Replay** is the default: a committed JSON fixture is required. No model or network access is used.
- **Record** is explicit: set `OLLAMACHAT_LLM_CASSETTE_RECORD=1` when running an integration test designed for recording. It must create a real client with the existing `LlmChatClientFactory` and call `CompleteAsync` only after every assertion succeeds.
- Tests that use synthetic fake clients for adapter unit coverage generate temporary cassette files and do not require network access.

Run the adapter tests (xUnit v3):

```sh
dotnet test ChatClient.Tests/ChatClient.Tests.csproj --filter-class "ChatClient.Tests.LlmCassetteTests"
```

The cassette schema is `schemaVersion: 1` with an ordered array of `exchanges` (method, request, response). Streaming updates stay separate and in order. Messages, function calls/results, relevant model options and function declarations are compared structurally. Object key ordering does not matter; array ordering, types and missing properties do. A mismatch reports the exchange number and first differing path. Unsupported content fails rather than being silently lost.

Provide `workspaceRoot` to `LlmCassette` in both modes to replace workspace path occurrences with `{{WORKSPACE_ROOT}}` and restore paths in replayed tool arguments. Make nondeterministic test inputs deterministic rather than introducing additional masks.

`CompleteAsync` checks exchange exhaustion and atomically publishes Record output. It does not run from `Dispose`, and an exception, interrupted stream or cancellation prevents publishing. The same file must never be recorded concurrently. Review the JSON diff before committing a fixture; never record real user files or secrets.

**Integration fixture status:** The headless Saved Workspace / real File Access scenario requires a local function-calling LLM to create the first cassette. It is not part of the CI adapter unit suite until such a cassette has been reviewed and committed. This is a remaining acceptance item of the MVP.
