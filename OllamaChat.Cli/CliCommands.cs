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

public enum CliCommandKind { Help, ListAgents, Run }

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
    public IReadOnlyList<string> AllowedMcpTools { get; init; } = [];
    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class CliUsageException(string message) : Exception(message);

public static class CliCommands
{
    public const string HelpText = """
        OllamaChat CLI — run existing Saved Agents or Workflows without starting the web UI.

        Usage:
          ollamachat agents list
          ollamachat run (--agent NAME_OR_GUID | --workflow NAME_OR_GUID)
                          [--prompt TEXT | --prompt-file FILE]
                          [--workspace DIR | --workspace-id GUID]
                          [--input KEY=VALUE]... [--stdin]
                          [--allow-read] [--allow-write] [--allow-file-memory]
                          [--sandbox NAME_OR_GUID --allow-shell]
                          [--allow-mcp SERVER:TOOL | --allow-mcp binding:GUID:TOOL]...
                          [--server NAME_OR_GUID --model MODEL]
                          [--output FILE] [--overwrite] [--timeout 30s|15m|1h]
                          [--progress]
          ollamachat --help

        Agents require a prompt, a prompt-file or redirected stdin.
        Autonomous workflows can run with named inputs and no prompt.
        A workflow reads stdin only with --stdin; this prevents blocking in pipelines.
        Permissions apply only to the current execution.
        """;

    public static CliArguments Parse(string[] args, bool stdinRedirected)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            return new CliArguments(CliCommandKind.Help);
        if (args.SequenceEqual(["agents", "list"]))
            return new CliArguments(CliCommandKind.ListAgents);
        if (args.Length < 1 || args[0] != "run")
            throw new CliUsageException("Expected 'run' or 'agents list'.");

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var mcpTools = new List<string>();
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            if (key is "--overwrite" or "--allow-read" or "--allow-write" or
                "--allow-shell" or "--allow-file-memory" or "--progress" or "--stdin")
            {
                if (!flags.Add(key))
                    throw new CliUsageException("Duplicate option: " + key);
                continue;
            }
            if (key is not ("--agent" or "--workflow" or "--prompt" or "--prompt-file" or
                "--workspace" or "--workspace-id" or "--output" or "--timeout" or
                "--sandbox" or "--server" or "--model" or "--allow-mcp" or "--input"))
                throw new CliUsageException("Unknown option: " + key);
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                throw new CliUsageException("Missing value for " + key + ".");
            var value = args[index];
            if (key == "--allow-mcp")
            {
                if (!value.Contains(':', StringComparison.Ordinal))
                    throw new CliUsageException("--allow-mcp requires SERVER:TOOL or binding:GUID:TOOL.");
                if (mcpTools.Contains(value, StringComparer.OrdinalIgnoreCase))
                    throw new CliUsageException("Duplicate --allow-mcp tool.");
                mcpTools.Add(value);
            }
            else if (key == "--input")
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
        if (string.IsNullOrWhiteSpace(agent) == string.IsNullOrWhiteSpace(workflow))
            throw new CliUsageException("Specify exactly one of --agent and --workflow.");
        options.TryGetValue("--prompt", out var prompt);
        options.TryGetValue("--prompt-file", out var promptFile);
        if (prompt is not null && promptFile is not null)
            throw new CliUsageException("Specify either --prompt or --prompt-file, not both.");
        if (flags.Contains("--stdin") && (prompt is not null || promptFile is not null))
            throw new CliUsageException("--stdin cannot be combined with an explicit prompt source.");
        if (flags.Contains("--stdin") && !stdinRedirected)
            throw new CliUsageException("--stdin requires redirected stdin.");
        if (agent is not null && prompt is null && promptFile is null && !stdinRedirected)
            throw new CliUsageException("An agent requires --prompt, --prompt-file or redirected stdin.");
        if (prompt is not null && string.IsNullOrWhiteSpace(prompt))
            throw new CliUsageException("Prompt must not be empty.");
        if (agent is not null && inputs.Count != 0)
            throw new CliUsageException("--input is supported only with --workflow.");

        var timeout = TimeSpan.FromMinutes(15);
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
        if ((server is null) != (model is null))
            throw new CliUsageException("--server and --model must be provided together.");

        options.TryGetValue("--output", out var output);
        if (flags.Contains("--overwrite") && output is null)
            throw new CliUsageException("--overwrite requires --output.");
        if (flags.Contains("--allow-shell") && !flags.Contains("--allow-write"))
            throw new CliUsageException("--allow-shell also requires --allow-write (shell can modify its mounted workspace).");

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
            AllowedMcpTools = mcpTools,
            Inputs = inputs,
            Progress = flags.Contains("--progress")
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

        if (selectedAgent is not null && (selectedAgent.LlmId is not Guid serverId ||
            string.IsNullOrWhiteSpace(selectedAgent.ModelName) ||
            await services.GetRequiredService<ILlmServerConfigService>().GetByIdAsync(serverId) is null) &&
            options.Server is null)
        {
            await error.WriteLineAsync("Saved Agent model or LLM server is not configured.");
            return CliExitCodes.Preflight;
        }

        ServerModelSelection? modelOverride = null;
        if (options.Server is not null)
        {
            var servers = await services.GetRequiredService<ILlmServerConfigService>().GetAllAsync();
            var matches = servers.Where(server =>
                (Guid.TryParse(options.Server, out var candidateId) && server.Id == candidateId) ||
                string.Equals(server.Name, options.Server, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || matches[0].Id is not Guid selectedServerId)
            {
                await error.WriteLineAsync("LLM server is missing or ambiguous. Use its GUID.");
                return CliExitCodes.Preflight;
            }
            modelOverride = new ServerModelSelection(selectedServerId, options.Model);
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
            options.AllowRead, options.AllowWrite, options.AllowShell, options.AllowFileMemory,
            options.AllowedMcpTools);
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
