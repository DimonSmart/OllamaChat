# OllamaChat

OllamaChat is a **C# server-side chat application** that uses **Semantic Kernel** to run:

- single-agent chat sessions,
- multi-agent orchestration sessions,
- local and remote LLM connections,
- RAG-assisted responses with uploaded files.

The project is designed as an experimentation and productization base for AI chat systems where you can switch between providers (Ollama and OpenAI-compatible servers), compare behavior, and evolve toward agent-first architecture.

## What the application does

OllamaChat provides a browser UI and backend services for managing and running chats with configurable AI agents.

Core capabilities:

- **Single-agent chat** for standard assistant workflows.
- **Multi-agent chat** built on Semantic Kernel orchestration for round-robin and strategy-driven conversations.
- **Model/server configuration** to connect to local Ollama instances and OpenAI-compatible APIs.
- **Server connectivity checks** before running workloads.
- **File upload + RAG indexing/search** to inject document context into chat history.
- **MCP integration and indexing** for function/tool discovery.
- **Streaming responses** with cancellation and message state updates.

## Current architecture (high level)

The solution is split into projects:

- `ChatClient.Api` – ASP.NET Core host, Blazor Server UI, controllers, orchestration services, app startup.
- `ChatClient.Application` – application services and interfaces.
- `ChatClient.Domain` – domain models used across the app.
- `ChatClient.Infrastructure` – repositories and persistence-oriented components.
- `ChatClient.Tests` – test project.

Dependency injection is configured in `ChatClient.Api/ServiceCollectionExtensions.cs`, where chat services, Semantic Kernel services, RAG services, configuration repositories, and MCP services are registered.

## Runtime model

At startup, the application:

1. initializes seed data for agents, LLM server configs, and MCP server configs,
2. checks Ollama availability,
3. builds MCP function index when available,
4. starts the web UI and API endpoints.

During a chat request, the app:

1. creates/loads session state,
2. builds chat history (including optional tool/RAG context),
3. runs orchestration (single-agent or group-chat strategy),
4. streams assistant and agent messages back to the UI.

## Key documentation

- Multi-agent orchestration details: `docs/multi-agent-chat.md`
- Agentic Framework migration strategy (parallel engine approach): `docs/agentic-migration-plan.md`
- Publishing notes: `docs/Publishing.md`

## Migration direction

This repository currently uses Semantic Kernel as the production engine. The recommended next phase is to build an **independent Agentic Framework engine in parallel** (separate services/classes and separate UI entry point/tab), run side-by-side with existing functionality, and migrate traffic incrementally based on quality and regression metrics.
