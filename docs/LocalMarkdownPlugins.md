# Local Codex Markdown plugins

OllamaChat registers existing local Codex plugin directories without installing,
copying, or modifying them. Only Markdown Agent Skills and passive resources are
supported. Marketplaces, hooks, scripts, IDD routing, and subagent delegation
are not installed.

Expected tree:

    idd-intent/
      .codex-plugin/plugin.json
      skills/idd-help/SKILL.md
      skills/idd-help/references/methodology.md
      idd-plugin.json

The Codex manifest contains name, version, description, and skills string
properties (skills normally points to ./skills/). Additional fields are ignored.
The skills directory must remain inside the registered plugin root.
The extra idd-plugin.json file is ignored. Native Agent Framework Skill
sources resolve passive references on demand without copying files.

## Setup

1. In the Intent-Driven-Development checkout, generate the Codex package:

       dotnet run --project tools/generate/Generate.csproj -- --version 0.0.0

2. Open Plugins in OllamaChat and register the absolute directory
   artifacts/marketplace/plugins/codex/idd-intent (not the marketplace root).
3. Open Skills Profiles and select idd-intent in Local Markdown plugins.
   Existing directory sources can remain selected.
4. Attach the profile to a Saved Agent using a function-calling-capable model.
   Start a new chat to load the selected set of Skills.

Registrations are persisted in UserData/local_markdown_plugins.json (override
with LocalMarkdownPlugins:FilePath); profiles retain their existing data file.
Disabled or removed registrations do not contribute Skills to new sessions.
Already-running sessions retain their stable cached source. Missing directories,
bad manifests, invalid Skills, and conflicts are session diagnostics, not
fatal errors for other Skills.

Existing direct sources take precedence over plugin Skills. When selected
plugins have the same manifest name, the first selected one wins. Duplicate
Skill names use the same first-wins discovery behavior as existing sources.
Plugin Markdown is not concatenated into the system prompt.

## Live local LLM smoke test

Requires local Ollama, a Saved Agent configured with a function-calling-capable
local model, and an enabled idd-intent plugin selected in its Skills Profile.

Run from the OllamaChat checkout:

    pwsh ./scripts/Smoke-IddMarkdownPlugin.ps1 -Agent "YOUR_SAVED_AGENT" -Workspace "/path/to/workspace"

This invokes the real OllamaChat headless CLI. Additionally run the same prompt
in the GUI and inspect the Harness trace. Confirm that idd-help is loaded and
references/methodology.md is read by native Skills tools. A plausible answer
alone does not prove skill/resource loading, so trace inspection is mandatory.

Disable idd-intent, start a fresh session and verify idd-help is absent.
Re-enable, start another fresh session and verify it returns. A profile
containing only existing file sources must continue working unchanged.
No new File Access or Shell permissions are necessary for passive skill reads.
