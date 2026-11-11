using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
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
    TimeSpan Timeout = default);

public sealed class CliUsageException(string message) : Exception(message);

public static class CliCommands
{
    public const string HelpText = """
        OllamaChat CLI — run an existing Saved Agent without starting the web UI.

        Usage:
          ollamachat agents list
          ollamachat run --agent NAME_OR_GUID (--prompt TEXT | --prompt-file FILE | < stdin)
                          [--workspace DIRECTORY] [--output FILE] [--overwrite]
                          [--timeout 30s|15m|1h]
          ollamachat --help

        Defaults: 15m timeout. Exactly one task source is required.
        Saved workflows, interactive approvals, shell and write access are not supported.
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
        var overwrite = false;
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            if (key == "--overwrite")
            {
                if (overwrite)
                    throw new CliUsageException("Duplicate option: --overwrite.");
                overwrite = true;
                continue;
            }
            if (key is not ("--agent" or "--prompt" or "--prompt-file" or "--workspace" or "--output" or "--timeout"))
                throw new CliUsageException("Unknown option: " + key);
            if (options.ContainsKey(key))
                throw new CliUsageException("Duplicate option: " + key);
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                throw new CliUsageException("Missing value for " + key + ".");
            options.Add(key, args[index]);
        }

        options.TryGetValue("--agent", out var agent);
        if (string.IsNullOrWhiteSpace(agent))
            throw new CliUsageException("--agent is required.");
        options.TryGetValue("--prompt", out var prompt);
        options.TryGetValue("--prompt-file", out var promptFile);
        if ((prompt is not null ? 1 : 0) + (promptFile is not null ? 1 : 0) + (stdinRedirected ? 1 : 0) != 1)
            throw new CliUsageException("Specify exactly one of --prompt, --prompt-file or redirected stdin.");
        if (prompt is not null && string.IsNullOrWhiteSpace(prompt))
            throw new CliUsageException("Prompt must not be empty.");

        var timeout = TimeSpan.FromMinutes(15);
        if (options.TryGetValue("--timeout", out var duration) && !TryParseTimeout(duration, out timeout))
            throw new CliUsageException("Invalid --timeout. Use a positive duration such as 30s, 15m or 1h.");

        options.TryGetValue("--workspace", out var workspace);
        options.TryGetValue("--output", out var output);
        if (overwrite && output is null)
            throw new CliUsageException("--overwrite requires --output.");

        return new CliArguments(CliCommandKind.Run, agent, prompt, promptFile,
            stdinRedirected, workspace, output, overwrite, timeout);
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

        var matching = FindAgents(agents, options.Agent!);
        if (matching.Count == 0)
        {
            await error.WriteLineAsync("Saved Agent not found: " + options.Agent);
            return CliExitCodes.Preflight;
        }
        if (matching.Count > 1)
        {
            await error.WriteLineAsync("Ambiguous Saved Agent name. Use its GUID (see 'agents list').");
            return CliExitCodes.Preflight;
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

        if (options.Output is not null && !CanWriteOutput(options.Output, options.Overwrite))
        {
            await error.WriteLineAsync("Output target is unavailable, or already exists (use --overwrite).");
            return CliExitCodes.FileError;
        }

        string prompt;
        try
        {
            prompt = options.Prompt ?? (options.PromptFile is not null
                ? await File.ReadAllTextAsync(options.PromptFile, cancellationToken)
                : await input.ReadToEndAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync("Cannot read the task input.");
            return CliExitCodes.FileError;
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await error.WriteLineAsync("Task input is empty.");
            return CliExitCodes.Arguments;
        }

        var selectedAgent = matching[0];
        if (selectedAgent.LlmId is not Guid serverId || string.IsNullOrWhiteSpace(selectedAgent.ModelName) ||
            await services.GetRequiredService<ILlmServerConfigService>().GetByIdAsync(serverId) is null)
        {
            await error.WriteLineAsync("Saved Agent model or LLM server is not configured.");
            return CliExitCodes.Preflight;
        }

        var executor = services.GetRequiredService<IHeadlessAgentExecutor>();
        var result = await executor.ExecuteAsync(
            new HeadlessAgentRunRequest(matching[0].Id, prompt, DirectPath: workspace), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Succeeded)
        {
            var code = IsPreflightError(result.ErrorCode) ? CliExitCodes.Preflight : CliExitCodes.Runtime;
            await error.WriteLineAsync("Agent " + (code == CliExitCodes.Preflight ? "preflight" : "execution") +
                                       " failed (" + SafeCode(result.ErrorCode) + ").");
            return code;
        }

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
            or "interactive_approval_required" ||
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
