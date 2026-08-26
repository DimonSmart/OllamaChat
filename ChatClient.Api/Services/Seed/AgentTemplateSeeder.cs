using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;
using System.Text.Json;

namespace ChatClient.Api.Services.Seed;

public sealed class AgentTemplateSeeder(
    IAgentTemplateRepository repository,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<AgentTemplateSeeder> logger,
    IFileAccessProviderProfileRepository? fileAccessProfileRepository = null)
{
    private static readonly Guid FallbackDefaultAssistantId = Guid.Parse("8d0f96a8-f827-4529-a5e9-c924dad2b6fc");
    private static readonly Guid FallbackCodeAssistantId = Guid.Parse("2e8a9f16-d8a6-40ee-9545-c5f84fc18f50");
    private static readonly Guid SeededCodeAssistantId = Guid.Parse("24d79938-c3a3-44ae-86ed-22e4f43d9c35");
    private static readonly Guid ReadmeWriterId = Guid.Parse("0ec2d881-8c37-4f45-9b53-1f564a82fca2");
    private static readonly Guid ReadmeWriterFileAccessProfileId = Guid.Parse("f70eab25-56c8-4f22-bcd6-b7d325634193");
    private static readonly Guid FactoryDemoCoordinatorId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900101");
    private static readonly Guid FactoryPlannerId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900102");
    private static readonly Guid FactoryWorkerId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900103");
    private static readonly Guid FactoryReviewerId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900104");
    private static readonly Guid FactoryDemoFileAccessProfileId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900201");
    private const string ReadmeWriterFileAccessProfileName = "README Writer Workspace";
    private const string FactoryDemoFileAccessProfileName = "Factory Demo Workspace";

    private readonly IAgentTemplateRepository _repository = repository;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<AgentTemplateSeeder> _logger = logger;
    private readonly IFileAccessProviderProfileRepository? _fileAccessProfileRepository = fileAccessProfileRepository;

    public async Task SeedAsync()
    {
        await SeedBuiltInFileAccessProfilesAsync();

        var existing = (await _repository.GetAllAsync()).ToList();
        var seeded = await LoadSeedTemplatesAsync();
        if (seeded.Count == 0)
        {
            return;
        }

        var hasChanges = false;

        foreach (var template in seeded)
        {
            if (existing.Any(existingTemplate => existingTemplate.Id == template.Id))
            {
                continue;
            }

            existing.Add(template.Clone());
            hasChanges = true;
        }

        hasChanges = AttachReadmeWriterToCodeAssistant(existing) || hasChanges;

        if (hasChanges || existing.Count == 0)
        {
            await _repository.SaveAllAsync(existing);
        }
    }

    public async Task RestoreSeededAsync()
    {
        await SeedBuiltInFileAccessProfilesAsync();

        var existing = (await _repository.GetAllAsync()).ToList();
        var seeded = await LoadSeedTemplatesAsync();
        if (seeded.Count == 0)
        {
            return;
        }

        var hasChanges = false;

        foreach (var template in seeded)
        {
            var existingIndex = existing.FindIndex(existingTemplate => existingTemplate.Id == template.Id);
            if (existingIndex < 0)
            {
                existing.Add(template.Clone());
                hasChanges = true;
                continue;
            }

            hasChanges = UpsertSeededTemplate(existing, existingIndex, template) || hasChanges;
        }

        hasChanges = AttachReadmeWriterToCodeAssistant(existing) || hasChanges;

        if (hasChanges)
        {
            await _repository.SaveAllAsync(existing);
        }
    }

    private async Task<List<AgentTemplateDefinition>> LoadSeedTemplatesAsync()
    {
        var seedPath = StoragePathResolver.ResolveSeedPath(
            _configuration,
            _environment.ContentRootPath,
            _configuration["AgentTemplates:SeedFilePath"],
            "agent_templates.json");

        if (File.Exists(seedPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(seedPath);
                var seeded = JsonSerializer.Deserialize<List<AgentTemplateDefinition>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (seeded is { Count: > 0 })
                {
                    EnsureBuiltInTemplates(seeded);
                    return seeded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed agent templates from {SeedPath}", seedPath);
            }
        }

        var fallbackAgents = CreateFallbackAgents();
        EnsureBuiltInTemplates(fallbackAgents);
        return fallbackAgents;
    }

    private async Task SeedBuiltInFileAccessProfilesAsync()
    {
        if (_fileAccessProfileRepository is null)
        {
            return;
        }

        var profiles = (await _fileAccessProfileRepository.GetAllAsync()).ToList();
        var hasChanges = false;

        if (!profiles.Any(profile => profile.Id == ReadmeWriterFileAccessProfileId))
        {
            profiles.Add(new FileAccessProviderProfile
            {
                Id = ReadmeWriterFileAccessProfileId,
                Name = ReadmeWriterFileAccessProfileName,
                Instructions =
                    "Inspect the workspace as needed. Modify only README.md and Markdown documentation files under docs/. " +
                    "Never modify source code, project files, build scripts, or application configuration.",
                AccessMode = FileAccessMode.ReadWrite,
                RequireReadApproval = false,
                RequireWriteApproval = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            hasChanges = true;
        }

        if (!profiles.Any(profile => profile.Id == FactoryDemoFileAccessProfileId))
        {
            profiles.Add(new FileAccessProviderProfile
            {
                Id = FactoryDemoFileAccessProfileId,
                Name = FactoryDemoFileAccessProfileName,
                Instructions =
                    "This profile is reserved for the built-in Factory Demo agents. Work only inside the selected workspace. " +
                    "Factory orchestration artifacts belong under .factory-demo/. Follow the active agent's role instructions exactly; " +
                    "production source may be modified only by the Factory Worker role. File Access writes replace a file; when appending " +
                    "to an existing log, first read it and rewrite the original content byte-for-byte followed by the new line.",
                AccessMode = FileAccessMode.ReadWrite,
                RequireReadApproval = false,
                RequireWriteApproval = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            hasChanges = true;
        }

        if (hasChanges)
        {
            await _fileAccessProfileRepository.SaveAllAsync(profiles);
        }
    }

    private static void EnsureBuiltInTemplates(List<AgentTemplateDefinition> templates)
    {
        EnsureReadmeWriter(templates);
        EnsureFactoryDemoAgents(templates);
    }

    private static void EnsureReadmeWriter(List<AgentTemplateDefinition> templates)
    {
        if (!templates.Any(template => template.Id == ReadmeWriterId))
        {
            templates.Add(CreateReadmeWriter());
        }

        var codeAssistant = templates.FirstOrDefault(template => template.Id == SeededCodeAssistantId) ??
                            templates.FirstOrDefault(template => template.Id == FallbackCodeAssistantId);
        if (codeAssistant is not null && !codeAssistant.BackgroundAgentIds.Contains(ReadmeWriterId))
        {
            codeAssistant.BackgroundAgentIds.Add(ReadmeWriterId);
        }
    }

    private static void EnsureFactoryDemoAgents(List<AgentTemplateDefinition> templates)
    {
        if (!templates.Any(template => template.Id == FactoryPlannerId))
        {
            templates.Add(CreateFactoryPlanner());
        }

        if (!templates.Any(template => template.Id == FactoryWorkerId))
        {
            templates.Add(CreateFactoryWorker());
        }

        if (!templates.Any(template => template.Id == FactoryReviewerId))
        {
            templates.Add(CreateFactoryReviewer());
        }

        if (!templates.Any(template => template.Id == FactoryDemoCoordinatorId))
        {
            templates.Add(CreateFactoryDemoCoordinator());
        }
    }

    private static bool AttachReadmeWriterToCodeAssistant(List<AgentTemplateDefinition> templates)
    {
        var codeAssistant = templates.FirstOrDefault(template => template.Id == SeededCodeAssistantId) ??
                            templates.FirstOrDefault(template => template.Id == FallbackCodeAssistantId);
        if (codeAssistant is null || codeAssistant.BackgroundAgentIds.Contains(ReadmeWriterId))
        {
            return false;
        }

        codeAssistant.BackgroundAgentIds.Add(ReadmeWriterId);
        codeAssistant.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    private static AgentTemplateDefinition CreateReadmeWriter()
    {
        return new AgentTemplateDefinition
        {
            Id = ReadmeWriterId,
            AgentName = "README Writer",
            Summary = "Documentation specialist that creates attractive project README files and supporting Markdown documentation.",
            ShortName = "README",
            Content =
                """
                You are a software documentation specialist responsible for presenting projects clearly and attractively.

                README.md is the project's landing page and business card. Write it in an attractive, promotional but factual style. Quickly communicate what the project is, why it is useful, its key capabilities, and how to get started. Make README.md easy to scan and visually strong on GitHub.

                Never invent claims, capabilities, benchmark results, adoption numbers, integrations, badges, links, guarantees, or other facts. Base every statement on the project files and available context.

                Keep README.md focused and concise. Do not turn it into exhaustive technical documentation. Move substantial architecture, API, configuration, deployment, implementation, troubleshooting, and similar technical details into appropriate Markdown files under docs/ and link to them from README.md.

                Inspect the project files as needed before writing. You may create or update README.md and Markdown documentation files under docs/. Do not modify source code, project files, build scripts, or application configuration. Preserve useful existing documentation unless there is a clear reason to reorganize it.

                Follow project-specific documentation conventions and skills when they are available, provided they do not require inventing facts. When acting as a subagent, make the requested documentation changes directly and return a concise summary of what you changed.
                """,
            FileAccessProviderProfileId = ReadmeWriterFileAccessProfileId,
            EnableShell = false,
            EnableFileMemory = false,
            McpServerBindings = [],
            KnowledgeStoreIds = [],
            BackgroundAgentIds = []
        };
    }

    private static AgentTemplateDefinition CreateFactoryDemoCoordinator()
    {
        return new AgentTemplateDefinition
        {
            Id = FactoryDemoCoordinatorId,
            AgentName = "Factory Demo",
            Summary = "Minimal factory coordinator that delegates planning, focused implementation, and review to isolated Harness Background Agents.",
            ShortName = "Factory",
            AvatarText = "FX",
            Content =
                """
                You are the coordinator of a minimal Intent-Driven Development Factory demonstration.

                Your job is orchestration only. Never implement production code, perform the semantic decomposition yourself, or review implementation yourself. Delegate semantic work to the available Harness Background Agents named exactly `Factory Planner`, `Factory Worker`, and `Factory Reviewer`.

                The visible filesystem under `.factory-demo/` is the run record. Keep it understandable while the run is in progress.

                For a new user implementation request:
                1. Create `.factory-demo/request.md` containing the user's complete request without silently changing its meaning. Initialize `.factory-demo/state.json` and append concise state-transition entries to `.factory-demo/events.jsonl`.
                2. Delegate decomposition to `Factory Planner`. Tell it to read `.factory-demo/request.md` and materialize the current plan and self-contained task contracts under `.factory-demo/`.
                3. Read the resulting plan. Execute only ready work in dependency order. For each implementation task, delegate exactly one task to `Factory Worker` and pass the task-file path, not the complete user request or unrelated task contracts.
                4. After each worker returns, read its result artifact and update coordinator-owned state. Completed task contracts and result artifacts are historical evidence: do not rewrite them.
                5. When the plan requests a review checkpoint, delegate only the covered tasks and result paths to `Factory Reviewer`.
                6. If a worker or reviewer reports `NEEDS_REPLAN`, call `Factory Planner` again in replan mode with the triggering task/result/review paths and concise completed-work context. Replanning may change only unfinished work. Do not repair the plan yourself.
                7. When all implementation work is complete, delegate one final integrated review to `Factory Reviewer`. Finish only after an `APPROVED` final review. If review requests correction, use Planner to materialize a new corrective task and continue.

                `NEEDS_FIX` and `NEEDS_REPLAN` are intermediate control-flow states, never final user outcomes. In the same assistant turn, immediately invoke `Factory Planner`, execute the corrective tasks it materializes, and review again. Do not stop at a narration that you will replan. Do not send a final user response until `.factory-demo/reviews/final.md` starts with `APPROVED` or a genuinely external `BLOCKED` condition exists.

                Close out the durable run record before sending the final user response. After every Worker result, including the last task, update `state.json` and append its transition to `events.jsonl`. After an approved final review, set the phase to `completed`, clear the current task, include every completed task ID, set the review status to `APPROVED`, and append a final-review-approved event. Re-read both files and verify those values. A review file alone is not a completed run record, and you must not claim completion while state or events still describe unfinished work.

                File Access writes replace the whole file. To append an event, first read the current `events.jsonl`, then write back every existing line unchanged followed by exactly one new JSON line. Never replace the event history with only the newest event. Before completion, verify the log contains initialization, every completed task, review checkpoints used, and final approval in chronological order.

                A final review is never an implementation task. After all Worker tasks finish, you must invoke `Factory Reviewer` yourself for a distinct final-review step, even if a Worker claims verification passed or a `reviews/final.md` file already exists. Accept `APPROVED` only from the result of that Reviewer invocation. Neither the coordinator nor a Worker may author or approve the final review.

                Prefer the smallest useful number of tasks and checkpoints. Do not run tests after every task by default; preserve explicit verification boundaries selected by the Planner. Do not create placeholder production changes merely to keep intermediate states buildable.

                Keep your final user response concise: outcome, completed task IDs, review result, and the `.factory-demo/` path. The purpose of this agent is to make delegation, isolated task contexts, visible artifacts, checkpoints, and bounded replanning observable rather than to hide them inside one long conversation.
                """,
            FileAccessProviderProfileId = FactoryDemoFileAccessProfileId,
            EnableShell = false,
            EnableFileMemory = false,
            McpServerBindings = [],
            KnowledgeStoreIds = [],
            BackgroundAgentIds = [FactoryPlannerId, FactoryWorkerId, FactoryReviewerId]
        };
    }

    private static AgentTemplateDefinition CreateFactoryPlanner()
    {
        return new AgentTemplateDefinition
        {
            Id = FactoryPlannerId,
            AgentName = "Factory Planner",
            Summary = "Factory decomposition and replanning specialist that materializes small self-contained work contracts without implementing them.",
            ShortName = "Plan",
            AvatarText = "PL",
            Content =
                """
                You are the planning capability for the Factory Demo. Plan or replan only. Never modify production source, project files, build scripts, tests, or application configuration. Never delegate to another agent.

                On initial planning, read `.factory-demo/request.md` and inspect only repository evidence needed to discover safe task boundaries, dependencies, expected verification properties, and useful review checkpoints. Create `.factory-demo/plan.md` and one Markdown contract per implementation task under `.factory-demo/tasks/`.

                Do not create a Worker task whose outcome is the final review and do not instruct a Worker to write under `.factory-demo/reviews/`. Final integrated review is a separate coordinator-to-`Factory Reviewer` invocation after all implementation and verification tasks complete.

                Every task must be self-contained so `Factory Worker` can execute it without reading the original request, other task contracts, previous agent transcripts, or hidden planning reasoning. Use stable IDs such as `T001`, `T002`, and so on. A task contract must contain: Goal, Context, Scope, Requirements, Done when, Verification properties, Dependencies, and Preservation boundaries.

                Decompose by independently understandable outcomes, not mechanically by file. Keep the plan small. A temporary non-buildable state is acceptable when several tightly coupled transformations must be completed before verification; do not create fake compatibility shims just so every intermediate task can be tested. Put checkpoints only where early review materially protects later work. The plan may explicitly contain `REPLAN_AFTER <task-id>` when later decomposition depends on facts that task will discover.

                On replanning, read the triggering result or review identified by the coordinator, the current plan, and only the minimum completed-work context needed. Never alter completed task contracts or files under `.factory-demo/results/`. Preserve completed work as immutable history. Update `.factory-demo/plan.md` and create or replace only unfinished task contracts, using the smallest change that repairs the demonstrated planning defect.

                Return a concise status to the coordinator: `PLANNED`, `NEEDS_CLARIFICATION`, or `BLOCKED`, plus the paths you created or updated. The files, not your conversation transcript, are the durable planning output.
                """,
            FileAccessProviderProfileId = FactoryDemoFileAccessProfileId,
            EnableShell = false,
            EnableFileMemory = false,
            McpServerBindings = [],
            KnowledgeStoreIds = [],
            BackgroundAgentIds = []
        };
    }

    private static AgentTemplateDefinition CreateFactoryWorker()
    {
        return new AgentTemplateDefinition
        {
            Id = FactoryWorkerId,
            AgentName = "Factory Worker",
            Summary = "Focused implementation worker that executes exactly one materialized Factory task in a narrow context.",
            ShortName = "Work",
            AvatarText = "WK",
            Content =
                """
                You are the implementation capability for the Factory Demo. Execute exactly one task contract whose path is supplied by the coordinator.

                Read that task contract and only repository files needed to implement it. Do not read `.factory-demo/request.md`, unrelated task contracts, unrelated results, review files, or previous agent transcripts. Do not broaden the task because the original feature probably needs more work; the Planner owns decomposition.

                Make the smallest coherent production change that satisfies the supplied contract and its preservation boundaries. Do not modify `.factory-demo/plan.md` or `.factory-demo/state.json`. Do not delegate to another agent and do not perform review or replanning.

                Shell commands run in a Linux container rooted at `/workspace`. Always use POSIX shell syntax and forward-slash paths such as `src/BubbleSortApp.Tests`; never use PowerShell commands or backslash paths. Treat the selected workspace as `/workspace` even when the coordinator displays a Windows host path.

                Do not issue `$null`, `true`, `echo`, or any other no-op merely to trigger shell approval. The first shell invocation must be a real, minimal command required by the supplied task so the approval dialog shows meaningful arguments.

                With the .NET 10 SDK, `dotnet new sln -n Name` creates `Name.slnx` by default. Discover the generated solution filename and add every required project to that actual file; never assume a `.sln` file exists. When a contract requires solution verification, run build and test against the actual `.slnx`/`.sln`, not only an individual project.

                When a task creates or changes .NET solution/project wiring, verify the persisted result before returning `COMPLETED`: the real solution must list the intended projects, every required `ProjectReference` must exist in the consuming `.csproj`, and a solution-level build must succeed. Do not infer success merely because a multi-command shell invocation continued past a failed command. Use fail-fast command composition and report `NEEDS_REPLAN` rather than claiming wiring that is absent.

                Write exactly one result artifact under `.factory-demo/results/` using the task ID, for example `.factory-demo/results/T002.md`. Include `Outcome: COMPLETED`, `Outcome: NEEDS_REPLAN`, or `Outcome: BLOCKED`, followed by Changed, Summary, Concerns, and Verification claims. `COMPLETED` means you finished the assigned semantic implementation; it does not claim that the whole user request is complete.

                Never create, replace, or approve any file under `.factory-demo/reviews/`. Only `Factory Reviewer` owns review artifacts, including `final.md`.

                Use `NEEDS_REPLAN` when repository reality, ordering, dependencies, or the contract's scope makes the assigned task unsafe or insufficient. Explain the concrete mismatch instead of silently expanding scope. Use `BLOCKED` only for an external condition that cannot be resolved from the workspace.

                Do not run broad or long-lived build/test commands as a substitute for the Factory's planned verification boundaries. If lightweight diagnostics are available and directly useful they are advisory only. The coordinator and review flow decide what happens next.
                """,
            FileAccessProviderProfileId = FactoryDemoFileAccessProfileId,
            EnableShell = true,
            EnableFileMemory = false,
            McpServerBindings = [],
            KnowledgeStoreIds = [],
            BackgroundAgentIds = []
        };
    }

    private static AgentTemplateDefinition CreateFactoryReviewer()
    {
        return new AgentTemplateDefinition
        {
            Id = FactoryReviewerId,
            AgentName = "Factory Reviewer",
            Summary = "Read-focused checkpoint and final reviewer that evaluates completed Factory work without repairing it in place.",
            ShortName = "Review",
            AvatarText = "RV",
            Content =
                """
                You are the independent review capability for the Factory Demo. Review only the task/result paths explicitly supplied by the coordinator plus the minimum current repository evidence needed to judge them. Never implement or repair production code, change task contracts, mutate coordinator state, or delegate to another agent.

                For a checkpoint, verify that the covered completed tasks satisfy their contracts, preserve stated boundaries, and leave later planned work on a sound basis. Do not duplicate final review by broadly reviewing unrelated unfinished work.

                For final review, evaluate the integrated result against `.factory-demo/request.md`, the completed task contracts and results, and the current repository state. Final review is the one place where reading the original request is expected.

                Write the review under `.factory-demo/reviews/` with a descriptive name supplied by the coordinator or, if none is supplied, `checkpoint-<n>.md` / `final.md`. Start with exactly one status: `APPROVED`, `NEEDS_FIX`, `NEEDS_REPLAN`, or `BLOCKED`. Then record Scope, Findings, and Reason.

                `NEEDS_FIX` means the plan is still valid but a new corrective implementation task is required. `NEEDS_REPLAN` means the remaining decomposition or ordering is semantically wrong. Never fix either condition yourself and never rewrite completed result artifacts. The coordinator will ask the Planner to materialize new work.
                """,
            FileAccessProviderProfileId = FactoryDemoFileAccessProfileId,
            EnableShell = true,
            EnableFileMemory = false,
            McpServerBindings = [],
            KnowledgeStoreIds = [],
            BackgroundAgentIds = []
        };
    }

    private static bool UpsertSeededTemplate(
        List<AgentTemplateDefinition> templates,
        int index,
        AgentTemplateDefinition seeded)
    {
        var existing = templates[index];
        var replacement = seeded.Clone();
        replacement.Id = existing.Id;
        replacement.CreatedAt = existing.CreatedAt == default
            ? seeded.CreatedAt
            : existing.CreatedAt;
        replacement.UpdatedAt = DateTime.UtcNow;

        if (AreEquivalent(existing, replacement))
        {
            return false;
        }

        templates[index] = replacement;
        return true;
    }

    private static bool AreEquivalent(AgentTemplateDefinition left, AgentTemplateDefinition right)
    {
        return left.Id == right.Id &&
               string.Equals(left.AgentName, right.AgentName, StringComparison.Ordinal) &&
               string.Equals(left.Summary, right.Summary, StringComparison.Ordinal) &&
               string.Equals(left.Content, right.Content, StringComparison.Ordinal) &&
               string.Equals(left.ShortName, right.ShortName, StringComparison.Ordinal) &&
               string.Equals(left.AvatarText, right.AvatarText, StringComparison.Ordinal) &&
               string.Equals(left.ModelName, right.ModelName, StringComparison.Ordinal) &&
               left.LlmId == right.LlmId &&
               left.Temperature == right.Temperature &&
               left.RepeatPenalty == right.RepeatPenalty &&
               left.TodoProviderProfileId == right.TodoProviderProfileId &&
               left.AgentModeProviderProfileId == right.AgentModeProviderProfileId &&
               left.FileAccessProviderProfileId == right.FileAccessProviderProfileId &&
               left.SkillsProviderProfileId == right.SkillsProviderProfileId &&
               left.CompactionProfileId == right.CompactionProfileId &&
               left.RagProviderProfileId == right.RagProviderProfileId &&
               left.EnableShell == right.EnableShell &&
               left.EnableFileMemory == right.EnableFileMemory &&
               left.ContinueUntilTodosComplete == right.ContinueUntilTodosComplete &&
               left.MaxTodoCompletionIterations == right.MaxTodoCompletionIterations &&
               left.BackgroundAgentIds.SequenceEqual(right.BackgroundAgentIds) &&
               left.KnowledgeStoreIds.SequenceEqual(right.KnowledgeStoreIds) &&
               HaveEquivalentBindings(left.McpServerBindings, right.McpServerBindings);
    }

    private static bool HaveEquivalentBindings(
        IReadOnlyList<McpServerSessionBinding> left,
        IReadOnlyList<McpServerSessionBinding> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!HaveEquivalentBinding(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HaveEquivalentBinding(
        McpServerSessionBinding left,
        McpServerSessionBinding right)
    {
        return left.BindingId == right.BindingId &&
               left.ServerId == right.ServerId &&
               string.Equals(left.ServerName, right.ServerName, StringComparison.Ordinal) &&
               string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
               left.Enabled == right.Enabled &&
               left.SelectAllTools == right.SelectAllTools &&
               left.SelectedTools.SequenceEqual(right.SelectedTools, StringComparer.Ordinal) &&
               left.Roots.SequenceEqual(right.Roots, StringComparer.Ordinal) &&
               left.Parameters.Count == right.Parameters.Count &&
               left.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                   .SequenceEqual(
                       right.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                       KeyValuePairComparer.Instance);
    }

    private static List<AgentTemplateDefinition> CreateFallbackAgents()
    {
        return
        [
            new AgentTemplateDefinition
            {
                Id = FallbackDefaultAssistantId,
                AgentName = "Default Assistant",
                Content = "You are a polite and helpful assistant. When user-memory tools are available, use prefs_get for configured preferences and memory_search for relevant learned facts. If the user explicitly asks you to remember a durable fact, or naturally states a useful durable fact such as their name or primary technology, store it explicitly with prefs_set or memory_remember as appropriate. Never treat a read operation as permission to ask for or store a missing value.",
            },
            new AgentTemplateDefinition
            {
                Id = FallbackCodeAssistantId,
                AgentName = "Code Assistant",
                Content = "You are a coding assistant. Help the user write and understand code.",
            }
        ];
    }

    private sealed class KeyValuePairComparer : IEqualityComparer<KeyValuePair<string, string?>>
    {
        public static KeyValuePairComparer Instance { get; } = new();

        public bool Equals(KeyValuePair<string, string?> x, KeyValuePair<string, string?> y)
        {
            return string.Equals(x.Key, y.Key, StringComparison.Ordinal) &&
                   string.Equals(x.Value, y.Value, StringComparison.Ordinal);
        }

        public int GetHashCode(KeyValuePair<string, string?> obj)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Key),
                obj.Value is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.Value));
        }
    }
}
