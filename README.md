# OllamaChat

OllamaChat is a C# server-side chat application with an Agentic runtime.

It provides:

- single-agent chat,
- multi-agent chat with round-robin and summary strategy,
- local Ollama and OpenAI-compatible server connectivity,
- MCP tool integration (with tool policy: validation, timeout, retries),
- RAG indexing/search for uploaded files,
- streaming responses with cancellation and message state tracking.

## Solution Structure

- `ChatClient.Api` - ASP.NET Core host, Blazor Server UI, controllers, runtime services.
- `ChatClient.Application` - application contracts and orchestration abstractions.
- `ChatClient.Domain` - shared domain models.
- `ChatClient.Infrastructure` - repositories and persistence utilities.
- `ChatClient.Tests` - automated tests.

## Runtime Notes

On startup, the app seeds agent/LLM/MCP configs, checks Ollama availability, builds MCP function index, and then starts UI/API endpoints.

During chat execution, the Agentic pipeline builds history, injects optional RAG context, runs model/tool calls, and streams responses back to UI.

## Docs

- Migration plan (completed SK decommission phase): `docs/agentic-migration-plan.md`
- Historical multi-agent SK description (archive): `docs/multi-agent-chat.md`
- Publishing notes: `docs/Publishing.md`
