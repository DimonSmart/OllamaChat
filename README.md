# OllamaChat

OllamaChat is a **C# / .NET application for creating, configuring, and running AI agents and multi-agent workflows** with local and cloud-hosted language models. It provides a browser-based workspace for experimenting with agent instructions, tools, knowledge retrieval, and orchestration on top of **Microsoft Agent Framework**.

Despite its name, OllamaChat is not limited to Ollama or to ordinary LLM conversations. You can configure reusable agents, connect external tools and knowledge, and run either an individual agent or a team of specialized agents through the same chat interface. The project is a practical environment for learning and applying modern agent technologies in .NET.

## Features

### Agentic Chat

Run a saved agent or workflow from a common chat UI. An agent can **invoke tools and act on their results**, not just generate text.

- Stream responses, inspect tool activity and model reasoning when the runtime provides it, and cancel an active run.
- Review and explicitly approve or deny protected tool operations; supported approvals can also cover the current session.
- Inspect available direct-agent session state, retrieval diagnostics, and Harness execution traces.
- Copy conversations and manage automatically saved chats. Compatible direct-agent chats can restore their native Framework session; workflows resume from the saved conversation and current workflow definition.

### Reusable AI Agents

Create, edit, and reuse saved agent definitions instead of rebuilding an agent for every conversation. An agent can combine:

- **Instructions and model settings:** role, behavior, model server, model, and optional tuning.
- **Tools:** selected MCP server bindings and tool permissions.
- **Knowledge:** attached Knowledge Stores and an optional retrieval policy.
- **Harness capabilities:** Agent Skills, context compaction, TODO management, operating modes, and optional session-scoped File Memory.
- **Controlled actions:** File Access policies and an explicit opt-in for sandboxed shell execution.
- **Background Agents:** other saved agents made available for delegation by the Microsoft Agent Framework Harness.

Agent definitions are reusable across chats. Runtime resources such as a filesystem workspace and Docker sandbox are selected for a session, not embedded as unrestricted permissions in an agent definition. Background Agents use Framework-managed delegation; they are distinct from explicitly defined multi-agent workflows.

### Multi-Agent Workflows

Compose specialized agents into validated, reusable workflows. OllamaChat supports four orchestration patterns:

| Pattern | How it works |
| --- | --- |
| **Sequential** | Passes work through agents in a defined order. |
| **Concurrent** | Runs agents on parallel branches and aggregates their responses. |
| **Group Chat** | Coordinates multiple agents through round-robin or programmable participant selection. |
| **Handoff** | Transfers control between agents along configured handoff paths. |

For example, a research workflow could use **Researcher → Reviewer → Writer**: one agent gathers information, another checks it, and a third prepares the result.

The **Workflow Definitions** editor lets you create and edit C#-based programmable workflow definitions, reuse saved agents as participants, validate source before saving, and restore shipped examples. Workflows run in the regular Chat UI. Depending on their definition, they either wait for a user message (**interactive**) or begin executing from supplied launch inputs (**autonomous**).

Workflow execution uses Microsoft Agent Framework's orchestration primitives rather than a separate user-facing workflow chat system.

### Local and Cloud LLMs

Manage and test connections to:

- **Ollama** for locally served models.
- **OpenAI** and **Azure OpenAI**.
- Other endpoints that implement the supported **OpenAI-compatible API**.

Browse models by server and inspect reported capabilities, including function calling and image support. Model capabilities matter: tool use and features such as File Access, shell execution, File Memory, and Background Agents require a function-calling-capable model. OpenAI-compatible endpoints are not guaranteed to support every feature.

### MCP and Tools

Connect external **Model Context Protocol (MCP)** servers and choose which tools an agent can access. Server management supports local-command and remote SSE connections, selective bindings, and connection configuration.

Use the **MCP Playground** to select a server, fill in tool arguments from its schema, invoke a tool, and inspect the result without first attaching it to an agent.

Built-in MCP capabilities include web search and page retrieval, current time, arithmetic, user preferences and memory, and structured Markdown document reading and editing. A workflow-scoped state tool can expose specific workflow inputs and outputs when required. Built-in tools are opt-in and have their own scope and restrictions.

### Knowledge Stores and RAG

Create **independent Knowledge Stores** for retrieval-augmented generation (RAG):

- Upload documents, retain their source content, and index their Markdown representation into chunks and embeddings.
- Track indexing status and inspect search results with source attribution.
- Attach multiple stores to an agent, or share one store across several agents without duplicating its data.
- Configure retrieval through reusable **RAG Provider Profiles**, including when retrieval runs, semantic versus hybrid search, relevance filtering, result limits, and context budgets.

The current implementation uses **Ollama for embedding generation** and **SQLite-based vector storage (SQLiteVec)**, with full-text retrieval used in hybrid search. Agents receive relevant retrieved excerpts as reference context, not as trusted instructions. Agent Skills and File Memory are separate mechanisms; a Knowledge Store is not an agent's general-purpose filesystem or memory.

### Agent Skills

Attach reusable, file-based **Agent Skills** to saved Harness agents using configurable **Skills Provider Profiles**. Skills are discovered from workspace directories, installed directories, and optionally Claude-compatible `.claude/skills` folders, using `SKILL.md` files.

Microsoft Agent Framework provides progressive, on-demand access to skill instructions and accompanying resources such as `references`, `assets`, and `scripts`. Skills are discovered when a direct-agent session starts, not continuously reloaded. OllamaChat does **not** supply a separate script runner: script execution remains subject to Framework support and existing execution and approval restrictions.

See [Agent Skills](docs/AgentSkills.md) for discovery and source-order details.

### Context, Chats, and Memory

Several independent mechanisms help manage longer-running work:

- **Compaction Profiles** reduce the model's runtime context through configurable strategies such as tool-result compaction, summarization, history truncation, and sliding windows. A separate summarization model can be configured. Compaction does not remove messages from the visible transcript.
- **Saved Chats** persist conversation transcripts locally and ordinarily checkpoint completed turns automatically. A compatible direct-agent chat restores its native Harness session; workflow restoration uses the recorded transcript and a currently valid workflow definition.
- **File Memory** is an optional native Harness capability for retaining entries across turns **within the same agent session**. It is not cross-chat, durable user memory.
- **User Memory MCP tools**, when enabled, can explicitly store user preferences and learned facts across conversations.

Conversation history, compressed model context, session memory, and durable user preferences serve different purposes.

### File Access and Sandboxed Shell Execution

Grant agents deliberately scoped access to a session workspace:

- **File Access Profiles** control read-only versus read-write operations and approval requirements. Access is rooted in a selected workspace directory.
- **Shell execution** is an explicit agent opt-in and requires a workspace plus an available sandbox profile at launch.
- **Sandbox Profiles** currently support Docker containers with configurable images, network mode, CPU/memory/process limits, timeouts, and other execution restrictions.
- Shell commands run **inside the sandbox, never directly on the host**. File Access and a Docker shell can share the same workspace; protected operations use the chat approval flow.

These facilities are not unrestricted access to the user's computer. Sandbox isolation has practical limits, and Docker is needed only when running sandboxed shell workloads.

## Getting Started

### Prerequisites

- **.NET 10 SDK** and **Git** to run from source.
- A reachable LLM server or provider account. Install [Ollama](https://ollama.com/) if you want to use local models; it is not required for cloud-model chat.
- **Docker** only if you intend to use the Docker Sandbox for shell-enabled agents or workflows.

Knowledge Store embedding/indexing currently uses Ollama even if your chat model is hosted elsewhere.

### Run from Source

From a terminal:

```bash
git clone https://github.com/DimonSmart/OllamaChat.git
cd OllamaChat
dotnet run --project ChatClient.Api --launch-profile backend
```

Open **http://127.0.0.1:5080** in your browser.

The `backend` development launch profile binds to that loopback address, **does not automatically open a browser**, and keeps running independently of browser-tab connections. Check **http://127.0.0.1:5080/healthz** for a lightweight host-readiness response; it does not test model-server availability.

### Command-line agent execution

`OllamaChat.Cli` executes existing Saved Agents or Workflows using the same
runtime as the UI, without starting a browser, ASP.NET server, or scheduler.

```bash
dotnet build OllamaChat.Cli/OllamaChat.Cli.csproj
dotnet run --project OllamaChat.Cli -- agents list
dotnet run --project OllamaChat.Cli -- config set server LocalOllama
dotnet run --project OllamaChat.Cli -- config set model gpt-oss:20b
dotnet run --project OllamaChat.Cli -- config set agent Developer
dotnet run --project OllamaChat.Cli -- "Fix the build"
dotnet run --project OllamaChat.Cli -- -a Reviewer "Review this code"
dotnet run --project OllamaChat.Cli -- run --workflow ResearchWorkflow --input topic=agents
```

Published binaries can be called as `ollamachat`. The short prompt syntax
and the original `run` form are equivalent. Saved Agent and Workflow selection is
mutually exclusive. Names are case-insensitive; use GUIDs to disambiguate.

**Configuration precedence** (lowest to highest):

1. Global `<StorageRoot>/UserData/cli_settings.json`, where StorageRoot is resolved
   by the existing storage configuration or `OLLAMACHAT_STORAGE_ROOT`.
2. `.ollamachat.json` in the caller's **current** directory (no parent traversal).
3. Explicit command-line flags, affecting only the current invocation.

Files are optional, regular JSON, and settings inherit individually. Supported
keys are `agent`, `workflow`, `server`, `model`, `workspace`,
`workspaceId`, `sandbox`, `timeout`, `progress`, `inputs`, and global
`permissions` (`read`, `write`, `shell`, `fileMemory`). Project
permissions may **reduce**, but never increase, global permissions; explicit
flags may authorize more for the current invocation, still constrained by the
Saved Agent and its profiles. The project JSON is never used as application,
host, secret or storage configuration.

```json
{
  "agent": "Developer",
  "server": "LocalOllama",
  "model": "gpt-oss:20b",
  "timeout": "1h",
  "permissions": {
    "read": true,
    "write": true,
    "shell": true
  }
}
```

Set and inspect settings without editing the files:

```bash
ollamachat config set agent Reviewer --local
ollamachat config set model qwen3:8b --local
ollamachat config show
ollamachat config show --local
ollamachat config show --effective
ollamachat config unset model --local
```

A workspace defaults to `Directory.GetCurrentDirectory()`. Relative workspace
paths in local configuration and command arguments resolve against the caller's
current directory. Saved Workspace IDs remain supported. A selected server or
model may be overridden independently; the missing component inherits from the
Saved Agent, if available. Invalid or ambiguous configurations fail preflight.

**MCP tools are controlled solely by each Saved Agent's MCP bindings.**
Enabled bindings, selected tools, binding parameters and stored secrets are
loaded through the existing app services. In workflows each participant retains
its own bindings. There is no CLI MCP allowlist or MCP configuration; use the
existing UI to add or change MCP servers. The legacy `--allow-mcp` and
`--no-mcp` flags are rejected with an explanatory error.

File Access still requires permission (read/write) and a matching provider
profile. Shell requires both write and shell permission, an existing Sandbox
Profile, and Docker; host-shell fallback remains forbidden. An MCP operation
that needs interactive approval cannot bypass that approval in headless mode.
A configured File Memory permission remains root-run-scoped. Scheduled Tasks
retain their separate restricted policy.

Prompt sources remain `--prompt`, `--prompt-file`, redirected stdin for
agents, and explicit `--stdin` for workflows. Autonomous workflows can
run without a prompt if their definition allows it. `--output` preserves its
no-overwrite default. Timeout defaults to 15 minutes and can be set with
`--timeout` (e.g. `30s`, `15m`, `1h`). stdout contains only
the answer, while diagnostics and progress are written to stderr.

Exit codes: 0 success, 1 internal error, 2 invalid arguments, 3 configuration
or preflight failure, 4 runtime failure, 5 file I/O failure, 124 timeout,
130 Ctrl+C.


### Your First Chat

1. Start Ollama with a downloaded model, or prepare credentials for a supported cloud endpoint. For a local model, use `ollama pull MODEL_NAME` (substitute an available model name).
2. Open **LLM Servers** and check the seeded Ollama connection or add and test your own server connection.
3. Use **Models** to confirm that your model is discoverable and check its capabilities.
4. Open **Agent Templates** to configure a reusable agent, or select an existing saved agent in **Chat**.
5. Select the server/model and agent in **Chat**, provide any required launch settings, and send a message. Try a saved workflow next to see multiple agents collaborate.

For local-model installation and operation, refer to the [Ollama documentation](https://docs.ollama.com/).

## Installation

A **Windows x64** packaged ZIP is available from [GitHub Releases](https://github.com/DimonSmart/OllamaChat/releases). Download a published Windows archive, extract it, and run `ChatClient.Api.exe`. Packaged executable startup can open the application in your browser.

The repository also contains scripts and WinGet manifests for **building, packaging, validating, and testing local installations**. They are not evidence of a published WinGet Community Repository package. See [Publishing](docs/Publishing.md) for the build and packaging workflow. A published release may represent an older application version than the current source tree.

## Architecture

OllamaChat is built on **C# / .NET 10**, **ASP.NET Core**, and **server-side Blazor**. **Microsoft Agent Framework** supplies the Harness and agent/workflow execution facilities, while **Microsoft.Extensions.AI** and provider integrations connect to language models. **MCP** supplies the tool interoperability boundary, **SQLite/SQLiteVec** stores knowledge vectors, and **Docker** backs isolated shell execution.

The solution separates the web application and runtime integration from application contracts, models, and persistence:

| Project | Responsibility |
| --- | --- |
| `ChatClient.Api` | ASP.NET Core host, Blazor UI, runtime integration, agent/workflow execution, and tools. |
| `ChatClient.Application` | Application services, contracts, and orchestration abstractions. |
| `ChatClient.Domain` | Shared domain definitions and models. |
| `ChatClient.Infrastructure` | Persistent repositories, storage, and infrastructure utilities. |
| `ChatClient.Tests` | Automated tests for runtime, features, and persistence behavior. |

OllamaChat adds application configuration, UX, persistence, policies, and integrations; it does not reimplement every agent loop or model protocol provided by its dependencies.

## Documentation

- [Agent Skills](docs/AgentSkills.md) — skill sources, discovery, and Framework behavior.
- [Publishing](docs/Publishing.md) — Windows packaging, WinGet manifests, and local smoke testing.
- [Product Intent Index](.idd/intent/INDEX.md) — the current feature specifications and architecture decisions that guide development.

## License

Released under [The Unlicense](LICENSE), dedicating the software to the public domain.
