using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text;

namespace OllamaChat.Cli;

public static class CliExitCodes
{
    public const int Success = 0;
    public const int InternalError = 1;
    public const int Arguments = 2;
    public const int Preflight = 3;
    public const int Runtime = 4;
    public const int FileError = 5;
    public const int Timeout = 124;
    public const int Canceled = 130;
}

public enum CliCommandKind { Help, ListAgents, Run, Config }

public sealed record CliArguments(
    CliCommandKind Kind,
    string? Agent = null,
    string? Prompt = null,
    string? PromptFile = null,
    bool FromStdin = false,
    string? Workspace = null,
    string? Output = null,
    bool Overwrite = false,
    TimeSpan Timeout = default)
{
    public string? Workflow { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Sandbox { get; init; }
    public string? Server { get; init; }
    public string? Model { get; init; }
    public bool AllowRead { get; init; }
    public bool AllowWrite { get; init; }
    public bool AllowShell { get; init; }
    public bool AllowFileMemory { get; init; }
    public bool Progress { get; init; }
    public bool StdinRedirected { get; init; }
    public string? ConfigAction { get; init; }
    public string? ConfigKey { get; init; }
    public string? ConfigValue { get; init; }
    public bool ConfigLocal { get; init; }
    public bool ConfigEffective { get; init; }

    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class CliUsageException(string message) : Exception(message);

public static class CliCommands
{
    public const string HelpText = """
        OllamaChat CLI — run Saved Agents and Workflows without a web server.

        ollamachat "Fix the build"
        ollamachat -a Developer "Review the code"
        ollamachat run [--agent NAME | --workflow NAME] [--prompt TEXT | --prompt-file FILE]
                       [--workspace DIR | --workspace-id GUID] [--input KEY=VALUE]...
                       [--server NAME] [--model MODEL] [--sandbox NAME]
                       [--allow-read] [--allow-write] [--allow-shell] [--allow-file-memory]
                       [--timeout 30s|15m|1h] [--progress] [--stdin]
        ollamachat agents list
        ollamachat config set KEY VALUE [--local]
        ollamachat config unset KEY [--local]
        ollamachat config show [--local | --effective]

        Settings: global cli_settings.json, project .ollamachat.json, command options.
        MCP tools come from Saved Agent bindings configured in the UI.
        """;

    public static CliArguments Parse(string[] args, bool stdinRedirected)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            return new CliArguments(CliCommandKind.Help);
        if (args.SequenceEqual(["agents", "list"]))
            return new CliArguments(CliCommandKind.ListAgents);
        if (args[0] == "config") return ParseConfig(args);
        var offset = args[0] == "run" ? 1 : 0;

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        string? positionalPrompt = null;
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = offset; index < args.Length; index++)
        {
            var key = args[index];
            if (key is "--allow-mcp" or "--no-mcp")
                throw new CliUsageException("MCP bindings must be configured in the Saved Agent UI; --allow-mcp and --no-mcp are not supported.");
            if (key == "-a") key = "--agent";
            if (key is "--overwrite" or "--allow-read" or "--allow-write" or
                "--allow-shell" or "--allow-file-memory" or "--progress" or "--stdin")
            {
                if (!flags.Add(key))
                    throw new CliUsageException("Duplicate option: " + key);
                continue;
            }
            if (key is not ("--agent" or "--workflow" or "--prompt" or "--prompt-file" or
                "--workspace" or "--workspace-id" or "--output" or "--timeout" or
                "--sandbox" or "--server" or "--model" or "--input"))
            {
                if (key.StartsWith('-') || positionalPrompt is not null)
                    throw new CliUsageException("Unknown option or extra positional argument: " + key);
                positionalPrompt = key;
                continue;
            }
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                throw new CliUsageException("Missing value for " + key + ".");
            var value = args[index];
            if (key == "--input")
            {
                var separator = value.IndexOf('=');
                if (separator <= 0)
                    throw new CliUsageException("--input requires KEY=VALUE.");
                if (!inputs.TryAdd(value[..separator], value[(separator + 1)..]))
                    throw new CliUsageException("Duplicate workflow input: " + value[..separator]);
            }
            else if (!options.TryAdd(key, value))
                throw new CliUsageException("Duplicate option: " + key);
        }

        options.TryGetValue("--agent", out var agent);
        options.TryGetValue("--workflow", out var workflow);
        if (agent is not null && workflow is not null)
            throw new CliUsageException("--agent and --workflow cannot be combined.");
        options.TryGetValue("--prompt", out var prompt);
        options.TryGetValue("--prompt-file", out var promptFile);
        if (positionalPrompt is not null)
        {
            if (prompt is not null || promptFile is not null)
                throw new CliUsageException("Positional prompt cannot be combined with --prompt or --prompt-file.");
            prompt = positionalPrompt;
        }
        if (prompt is not null && promptFile is not null)
            throw new CliUsageException("Specify either --prompt or --prompt-file, not both.");
        if (flags.Contains("--stdin") && (prompt is not null || promptFile is not null))
            throw new CliUsageException("--stdin cannot be combined with an explicit prompt source.");
        if (flags.Contains("--stdin") && !stdinRedirected)
            throw new CliUsageException("--stdin requires redirected stdin.");
        if (prompt is not null && string.IsNullOrWhiteSpace(prompt))
            throw new CliUsageException("Prompt must not be empty.");
        if (agent is not null && inputs.Count != 0)
            throw new CliUsageException("--input is supported only with --workflow.");

        var timeout = default(TimeSpan);
        if (options.TryGetValue("--timeout", out var duration) && !TryParseTimeout(duration, out timeout))
            throw new CliUsageException("Invalid --timeout. Use a positive duration such as 30s, 15m or 1h.");

        options.TryGetValue("--workspace", out var workspace);
        options.TryGetValue("--workspace-id", out var workspaceId);
        if (workspace is not null && workspaceId is not null)
            throw new CliUsageException("--workspace and --workspace-id cannot be combined.");
        if (workspaceId is not null && (!Guid.TryParse(workspaceId, out var id) || id == Guid.Empty))
            throw new CliUsageException("--workspace-id must be a nonempty GUID.");

        options.TryGetValue("--server", out var server);
        options.TryGetValue("--model", out var model);

        options.TryGetValue("--output", out var output);
        if (flags.Contains("--overwrite") && output is null)
            throw new CliUsageException("--overwrite requires --output.");

        return new CliArguments(CliCommandKind.Run, agent, prompt, promptFile,
            flags.Contains("--stdin") || (agent is not null && prompt is null && promptFile is null && stdinRedirected),
            workspace, output, flags.Contains("--overwrite"), timeout)
        {
            Workflow = workflow,
            WorkspaceId = workspaceId,
            Sandbox = options.GetValueOrDefault("--sandbox"),
            Server = server,
            Model = model,
            AllowRead = flags.Contains("--allow-read"),
            AllowWrite = flags.Contains("--allow-write"),
            AllowShell = flags.Contains("--allow-shell"),
            AllowFileMemory = flags.Contains("--allow-file-memory"),
            Inputs = inputs,
            Progress = flags.Contains("--progress"),
            StdinRedirected = stdinRedirected
        };
    }

    private static CliArguments ParseConfig(string[] args)
    {
        if (args.Length < 2 || args[1] is not ("set" or "unset" or "show"))
            throw new CliUsageException("Expected config set, unset, or show.");
        var action = args[1];
        var local = args.Contains("--local");
        var effective = args.Contains("--effective");
        var values = args.Skip(2).Where(x => x is not ("--local" or "--effective")).ToArray();
        if ((local && effective) || (effective && action != "show") ||
            args.Length != values.Length + 2 + (local ? 1 : 0) + (effective ? 1 : 0) ||
            (action == "set" && values.Length != 2) ||
            (action == "unset" && values.Length != 1) ||
            (action == "show" && values.Length != 0))
            throw new CliUsageException("Invalid config command arguments.");
        return new CliArguments(CliCommandKind.Config)
        {
            ConfigAction = action,
            ConfigKey = values.ElementAtOrDefault(0),
            ConfigValue = values.ElementAtOrDefault(1),
            ConfigLocal = local,
            ConfigEffective = effective
        };
    }

    public static bool TryParseTimeout(string? value, out TimeSpan timeout)
    {
        timeout = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
            return false;
        var unit = value[^1];
        if (unit is not ('s' or 'm' or 'h') ||
            !long.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) ||
            quantity <= 0)
            return false;

        var secondsPerUnit = unit switch { 's' => 1L, 'm' => 60L, _ => 3600L };
        // CancelAfter uses a millisecond timer with a bounded supported range.
        if (quantity > (uint.MaxValue - 1L) / 1000L / secondsPerUnit)
            return false;
        timeout = TimeSpan.FromSeconds(quantity * secondsPerUnit);
        return true;
    }

    public static async Task<int> ExecuteAsync(CliArguments options, IServiceProvider services,
        TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var templates = services.GetRequiredService<IAgentTemplateService>();
        var agents = (await templates.GetAllAsync()).ToList();
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Kind == CliCommandKind.ListAgents)
        {
            await output.WriteLineAsync("ID                                    NAME");
            foreach (var agent in agents.OrderBy(a => a.AgentName, StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(a => a.Id))
                await output.WriteLineAsync(agent.Id.ToString("D") + "  " + agent.AgentName);
            return CliExitCodes.Success;
        }

        if (options.Kind != CliCommandKind.Run)
            return CliExitCodes.Success;

        if (options.Agent is null && options.Workflow is null)
        {
            await error.WriteLineAsync("No Saved Agent or Workflow selected. Set a default with 'config set agent NAME'.");
            return CliExitCodes.Preflight;
        }
        if (options.Agent is not null && options.Inputs.Count > 0)
        {
            await error.WriteLineAsync("Workflow inputs cannot be passed to an agent.");
            return CliExitCodes.Arguments;
        }
        Guid agentId = Guid.Empty;
        Guid? workflowId = null;
        AgentTemplateDefinition? selectedAgent = null;
        if (options.Workflow is not null)
        {
            var workflows = await services.GetRequiredService<IWorkflowDefinitionService>().GetAllAsync();
            var matching = FindWorkflows(workflows, options.Workflow);
            if (matching.Count != 1)
            {
                await error.WriteLineAsync(matching.Count == 0 ? "Saved Workflow not found." :
                    "Ambiguous Saved Workflow name. Use its GUID.");
                return CliExitCodes.Preflight;
            }
            workflowId = matching[0].Id;
        }
        else
        {
            var matching = FindAgents(agents, options.Agent!);
            if (matching.Count != 1)
            {
                await error.WriteLineAsync(matching.Count == 0 ? "Saved Agent not found: " + options.Agent :
                    "Ambiguous Saved Agent name. Use its GUID (see 'agents list').");
                return CliExitCodes.Preflight;
            }
            selectedAgent = matching[0];
            agentId = selectedAgent.Id;
        }

        string? workspace = null;
        if (options.Workspace is not null)
        {
            try { workspace = Path.GetFullPath(options.Workspace); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                await error.WriteLineAsync("Invalid workspace path.");
                return CliExitCodes.Preflight;
            }
        }

        Guid? savedWorkspaceId = null;
        if (options.WorkspaceId is not null)
            savedWorkspaceId = Guid.Parse(options.WorkspaceId);

        if (options.Output is not null && !CanWriteOutput(options.Output, options.Overwrite))
        {
            await error.WriteLineAsync("Output target is unavailable, or already exists (use --overwrite).");
            return CliExitCodes.FileError;
        }

        string prompt;
        try
        {
            prompt = options.Prompt ??
                (options.PromptFile is not null
                    ? await File.ReadAllTextAsync(options.PromptFile, cancellationToken)
                    : options.FromStdin ? await input.ReadToEndAsync(cancellationToken) : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync("Cannot read the task input.");
            return CliExitCodes.FileError;
        }
        if (options.Workflow is null && string.IsNullOrWhiteSpace(prompt))
        {
            await error.WriteLineAsync("Task input is empty.");
            return CliExitCodes.Arguments;
        }

        if (selectedAgent is not null && options.Server is null &&
            (selectedAgent.LlmId is not Guid agentServerId ||
             (string.IsNullOrWhiteSpace(selectedAgent.ModelName) && options.Model is null) ||
             await services.GetRequiredService<ILlmServerConfigService>()
                 .GetByIdAsync(agentServerId) is null))
        {
            await error.WriteLineAsync("Saved Agent model or LLM server is not configured.");
            return CliExitCodes.Preflight;
        }

        ServerModelSelection? modelOverride = null;
        if (options.Server is not null || options.Model is not null)
        {
            Guid? serverId = selectedAgent?.LlmId;
            if (options.Server is not null)
            {
                var servers = await services.GetRequiredService<ILlmServerConfigService>().GetAllAsync();
                var matches = servers.Where(server =>
                    (Guid.TryParse(options.Server, out var candidateId) && server.Id == candidateId) ||
                    string.Equals(server.Name, options.Server, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1 || matches[0].Id is not Guid matchedId)
                {
                    await error.WriteLineAsync("LLM server is missing or ambiguous. Use its GUID.");
                    return CliExitCodes.Preflight;
                }
                serverId = matchedId;
            }
            modelOverride = new ServerModelSelection(serverId,
                options.Model ?? selectedAgent?.ModelName);
        }

        Guid? sandboxId = null;
        if (options.Sandbox is not null)
        {
            var profiles = await services.GetRequiredService<ISandboxProfileService>().GetAllAsync();
            var matches = profiles.Where(profile =>
                (Guid.TryParse(options.Sandbox, out var id) && profile.Id == id) ||
                string.Equals(profile.Name, options.Sandbox, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
            {
                await error.WriteLineAsync("Sandbox Profile is missing or ambiguous. Use its GUID.");
                return CliExitCodes.Preflight;
            }
            sandboxId = matches[0].Id;
        }

        var policy = AgentExecutionPolicy.Cli(
            options.AllowRead, options.AllowWrite, options.AllowShell, options.AllowFileMemory);
        if (options.Progress)
            await error.WriteLineAsync("Starting headless execution...");

        var executor = services.GetService<IAgentExecutionService>() ??
            services.GetRequiredService<IHeadlessAgentExecutor>();
        var result = await executor.ExecuteAsync(
            new HeadlessAgentRunRequest(agentId, prompt, savedWorkspaceId, workspace,
                workflowId, policy, sandboxId, options.Inputs, modelOverride), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Succeeded)
        {
            var code = IsPreflightError(result.ErrorCode) ? CliExitCodes.Preflight : CliExitCodes.Runtime;
            await error.WriteLineAsync("Agent " + (code == CliExitCodes.Preflight ? "preflight" : "execution") +
                                       " failed (" + SafeCode(result.ErrorCode) + ").");
            return code;
        }
        if (options.Progress)
            await error.WriteLineAsync("Execution completed.");

        if (options.Output is null)
        {
            await output.WriteAsync(result.Text ?? string.Empty);
            await output.FlushAsync(cancellationToken);
            return CliExitCodes.Success;
        }

        try
        {
            await SaveOutputAsync(options.Output, result.Text ?? string.Empty, options.Overwrite, cancellationToken);
            return CliExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync("Cannot write the result file.");
            return CliExitCodes.FileError;
        }
    }

    public static IReadOnlyList<SavedWorkflowDefinition> FindWorkflows(
        IEnumerable<SavedWorkflowDefinition> workflows, string identifier)
    {
        if (Guid.TryParse(identifier, out var id))
            return workflows.Where(workflow => workflow.Id == id).ToArray();
        return workflows.Where(workflow => string.Equals(workflow.DisplayName, identifier,
            StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static IReadOnlyList<AgentTemplateDefinition> FindAgents(
        IEnumerable<AgentTemplateDefinition> agents, string identifier)
    {
        if (Guid.TryParse(identifier, out var id))
            return agents.Where(a => a.Id == id).ToArray();
        return agents.Where(a => string.Equals(a.AgentName, identifier,
            StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static bool IsPreflightError(string? code) =>
        code is "agent_not_found" or "agent_preflight_failed" or "headless_capability_unsupported"
            or "interactive_approval_required" or "agent_preflight_failed" or "sandbox_unavailable"
            or "policy_read_denied" or "policy_write_denied" or "policy_shell_denied"
            or "policy_memory_denied" or "invalid_input" ||
        (code?.StartsWith("workspace_", StringComparison.Ordinal) ?? false);

    private static string SafeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "unknown";
        return new string(code.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(80).ToArray());
    }

    private static bool CanWriteOutput(string path, bool overwrite)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return Directory.Exists(Path.GetDirectoryName(full)) && (overwrite || !File.Exists(full));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static async Task SaveOutputAsync(
        string path, string text, bool overwrite, CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("Output directory does not exist.");
        if (!overwrite && File.Exists(target))
            throw new IOException("Output file already exists.");

        var temporary = Path.Combine(parent, "." + Path.GetFileName(target) + "." +
                                                Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
