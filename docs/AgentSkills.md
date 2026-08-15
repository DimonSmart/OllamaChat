# Agent Skills

Skills Provider profiles select ordered workspace and installed file sources for a saved agent. New sources suggest the canonical `**/SKILL.md` pattern. OllamaChat resolves configured paths to skill directories and discovers them through the native Microsoft Agent Framework source once while the direct session is created:

```text
AgentSkillsProfile
        ↓
profile matching
        ↓
native file-backed discovery
        ↓
duplicate and validation resolution
        ↓
stable session-scoped AgentSkillsSource
        ↓
HarnessAgent
        ↓
AgentSkillsProvider
```

The complete skill directory is retained. A skill can therefore include `references`, `assets`, and `scripts`; OllamaChat does not copy their content into the system prompt or index them as RAG data. The framework retains progressive, on-demand disclosure and exposes supported resources through its own Agent Skills behavior. File Access is not required for skill resources, and RAG configuration is independent of Skills.

Configured workspace sources have precedence in profile order, followed by `.claude/skills`, then absolute sources in profile order. Duplicate names are resolved by the framework's first-wins source order.

The discovered native source is cached for the lifetime of the conversation/session, so filesystem changes do not alter an active session's skill set. Invalid and duplicate candidates are shown as session diagnostics. OllamaChat does not install a custom skill script runner. Scripts remain native file-backed skill scripts and are not executed outside the existing sandbox and approval model.
