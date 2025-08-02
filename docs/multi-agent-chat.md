# Multi-Agent Chat Implementation Plan

## Overview
This document outlines a proposal to extend **OllamaChat** with multi-agent conversations. Users will be able to compose a set of agents, give a dedicated prompt to a *manager* agent, and chat with all participants in a single conversation. Each agent is defined by a system prompt and may optionally target a specific model. If no model is specified, the chat's primary model is used.

## Current State
- System prompts are stored and edited through `SystemPrompt` objects that include name, content, and optional `AgentName` metadata.
- Chat interactions currently employ a single `KernelAgent` via a basic `DefaultAgentCoordinator` that always returns one agent.
- The system prompt editor (`SystemPrompts.razor`) exposes fields for prompt content and agent name but has no model selection.

## Design Goals
1. Let users create agents by pairing a system prompt with an optional model.
2. Allow selecting any number of agents plus one manager agent before starting a chat.
3. Display messages from multiple agents within the conversation UI, preserving compatibility with the existing single-agent flow.
4. Provide an extensible coordination layer so the manager agent can decide which agent responds next.

## Step-by-Step Plan
### 1. Extend prompt model and storage ✅
- Add a `ModelName` property to `SystemPrompt`. Update serialization and `SystemPromptService` CRUD logic to persist this value.
- Migrate existing prompt files so older entries default to `null`/empty `ModelName`.

### 2. Update system prompt editor ✅
- In `SystemPrompts.razor`, fetch available models (via `IOllamaClientService`) and present a dropdown when editing/creating prompts.
- Allow leaving the selection blank to fall back to the chat's main model.

### 3. Treat prompts as agents ✅
- When initializing a chat, create a `KernelAgent` instance for each selected prompt, passing along its model preference.
- Modify `ChatService` and `IChatService` to manage a list of active agents instead of a single prompt.

### 4. Manager agent and coordination
- Introduce a `ManagerAgent` derived from `AgentBase`. It receives the user's message and decides which agent should answer.
- Implement a `MultiAgentCoordinator` that holds the manager and worker agents. It exposes `GetNextAgent()` by consulting the manager's response or a simple policy (e.g., round-robin) until more advanced logic is provided.

### 5. Multi-agent chat UI
- Extend the chat-start screen to allow selection of multiple agents plus a manager prompt.
- During conversation, display messages with each agent's name/Avatar to distinguish speakers.
- Ensure a fallback to existing single-agent UI when only one agent is chosen.

### 6. Service and history updates
- Adjust `ChatHistoryBuilder` to track messages from multiple agents; include agent names when constructing `ChatHistory` entries.
- Ensure streaming responses and cancellation flow handle multiple simultaneous agent operations gracefully.

### 7. Testing
- Add unit tests verifying that `SystemPromptService` correctly stores and retrieves `ModelName`.
- Add tests for `MultiAgentCoordinator` to confirm agent rotation/selection logic.

### 8. Documentation and examples
- Create user-facing docs demonstrating how to create agents, assign models, and start a multi-agent chat.
- Provide sample prompts and configurations to help users bootstrap their own agent sets.

## Future Enhancements
- Persist per-agent conversation state or memory.
- Allow agents to reference each other's outputs and chain reasoning.
- Support importing/exporting agent configurations.

