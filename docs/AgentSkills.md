# Agent Skills

Skills Provider profiles select ordered workspace and installed file sources for a saved agent. OllamaChat resolves the configured `SKILL.md` paths to their containing directories, then passes those directories to the native Microsoft Agent Framework source:

```text
AgentSkillsProfile
        ↓
configured file sources
        ↓
AgentFileSkillsSource
        ↓
HarnessAgent
        ↓
AgentSkillsProvider
```

The complete skill directory is retained. A skill can therefore include `references`, `assets`, and `scripts`; OllamaChat does not copy their content into the system prompt or index them as RAG data. The framework retains progressive disclosure and exposes supported resources through its own Agent Skills behavior.

Configured workspace sources have precedence in profile order, followed by `.claude/skills`, then absolute sources in profile order. Duplicate names are resolved by the framework's first-wins source order.

OllamaChat does not install a custom skill script runner. Scripts remain native file-backed skill scripts, and can execute only if the framework receives a runner that can be safely integrated with the session sandbox and tool-approval model.
