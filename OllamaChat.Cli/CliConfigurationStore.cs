using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaChat.Cli;

/// <summary>CLI-only settings; project files never enter application/host configuration.</summary>
public sealed class CliConfigurationStore
{
    private static readonly HashSet<string> Settings = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent", "workflow", "server", "model", "workspace", "workspaceId",
        "sandbox", "timeout", "progress", "inputs",
        "permissions.read", "permissions.write", "permissions.shell",
        "permissions.fileMemory"
    };

    public string GlobalPath { get; }
    public string ProjectPath { get; }
    public string WorkingDirectory { get; }

    public CliConfigurationStore(IConfiguration applicationConfig, string workingDirectory)
        : this(Path.Combine(StoragePathResolver.GetStorageRoot(applicationConfig),
            "UserData", "cli_settings.json"), workingDirectory)
    {
    }

    public CliConfigurationStore(string globalPath, string workingDirectory)
    {
        GlobalPath = Path.GetFullPath(globalPath);
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        ProjectPath = Path.Combine(WorkingDirectory, ".ollamachat.json");
    }

    private static IConfigurationRoot Load(string path)
    {
        try
        {
            // Do not allow a malformed JSON file to silently reset the configuration.
            var result = AddFile(new ConfigurationBuilder(), path).Build();
            if (result.GetSection("permissions:mcpTools").Exists())
                throw new CliUsageException("MCP permissions are configured in the Saved Agent, not CLI JSON.");
            return result;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
        {
            throw new CliUsageException("Invalid CLI JSON configuration: " + path);
        }
    }

    private static IConfigurationBuilder AddFile(IConfigurationBuilder builder, string path) =>
        builder.AddJsonFile(new PhysicalFileProvider(Path.GetDirectoryName(path)!),
            Path.GetFileName(path), optional: true, reloadOnChange: false);

    private static string? Value(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value ? value : null;

    private static bool Permission(IConfiguration config, string key, bool fallback)
    {
        var value = Value(config, "permissions:" + key);
        if (value is null) return fallback;
        if (bool.TryParse(value, out var granted)) return granted;
        throw new CliUsageException("Invalid boolean for permissions." + key + ".");
    }

    private static (string?, string?) ChoosePair(
        string? cliA, string? cliB, IConfiguration local, IConfiguration global,
        string keyA, string keyB)
    {
        if (cliA is not null || cliB is not null)
            return (cliA, cliB);
        var localA = Value(local, keyA);
        var localB = Value(local, keyB);
        if (localA is not null && localB is not null)
            throw new CliUsageException($"Project configuration cannot select both {keyA} and {keyB}.");
        if (localA is not null || localB is not null)
            return (localA, localB);
        var globalA = Value(global, keyA);
        var globalB = Value(global, keyB);
        if (globalA is not null && globalB is not null)
            throw new CliUsageException($"Global configuration cannot select both {keyA} and {keyB}.");
        return (globalA, globalB);
    }

    public CliArguments Resolve(CliArguments command)
    {
        var global = Load(GlobalPath);
        var local = Load(ProjectPath);
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Override(string key, string? value)
        {
            if (value is not null) overrides[key] = value;
        }
        Override("server", command.Server);
        Override("model", command.Model);
        Override("sandbox", command.Sandbox);
        if (command.Timeout != default)
            Override("timeout", FormatTimeout(command.Timeout));
        if (command.Progress)
            Override("progress", "true");
        foreach (var entry in command.Inputs)
            Override("inputs:" + entry.Key, entry.Value);

        IConfiguration combined;
        try
        {
            combined = AddFile(AddFile(new ConfigurationBuilder(), GlobalPath), ProjectPath)
                .AddInMemoryCollection(overrides)
                .Build();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
        {
            throw new CliUsageException("Invalid CLI JSON configuration.");
        }

        var (agent, workflow) = ChoosePair(command.Agent, command.Workflow,
            local, global, "agent", "workflow");
        var (workspace, workspaceId) = ChoosePair(command.Workspace, command.WorkspaceId,
            local, global, "workspace", "workspaceId");
        if (workspaceId is not null && (!Guid.TryParse(workspaceId, out var id) || id == Guid.Empty))
            throw new CliUsageException("workspaceId must be a nonempty GUID.");

        var path = workspaceId is null
            ? Path.GetFullPath(workspace is null ? WorkingDirectory :
                Path.IsPathRooted(workspace) ? workspace : Path.Combine(WorkingDirectory, workspace))
            : null;

        var timeoutText = Value(combined, "timeout");
        if (timeoutText is not null && !CliCommands.TryParseTimeout(timeoutText, out _))
            throw new CliUsageException("Invalid timeout. Use a positive duration such as 30s, 15m or 1h.");
        var timeout = timeoutText is null ? TimeSpan.FromMinutes(15) :
            ParseTimeout(timeoutText);
        var progressText = Value(combined, "progress");
        if (progressText is not null && !bool.TryParse(progressText, out _))
            throw new CliUsageException("Invalid progress value (expected true or false).");

        // Project settings can only narrow permissions granted globally. The
        // invocation's explicit flags are user authorization for this one run.
        bool EffectivePermission(string name, bool explicitlyGranted) =>
            explicitlyGranted || (Permission(global, name, false) && Permission(local, name, true));
        var read = EffectivePermission("read", command.AllowRead);
        var write = EffectivePermission("write", command.AllowWrite);
        var shell = EffectivePermission("shell", command.AllowShell);
        var fileMemory = EffectivePermission("fileMemory", command.AllowFileMemory);
        if (shell && !write)
            throw new CliUsageException("Shell permission requires write permission.");

        var inputs = combined.GetSection("inputs").GetChildren()
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.OrdinalIgnoreCase);

        return command with
        {
            Agent = agent,
            Workflow = workflow,
            Workspace = path,
            WorkspaceId = workspaceId,
            Server = Value(combined, "server"),
            Model = Value(combined, "model"),
            Sandbox = Value(combined, "sandbox"),
            Timeout = timeout,
            Progress = progressText is not null && bool.Parse(progressText),
            AllowRead = read,
            AllowWrite = write,
            AllowShell = shell,
            AllowFileMemory = fileMemory,
            Inputs = inputs,
            FromStdin = command.FromStdin || (command.StdinRedirected &&
                agent is not null && command.Prompt is null && command.PromptFile is null)
        };
    }

    private static TimeSpan ParseTimeout(string value)
    {
        CliCommands.TryParseTimeout(value, out var result);
        return result;
    }

    private static string FormatTimeout(TimeSpan value) =>
        ((long)value.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";

    public string Show(bool localOnly, bool effective)
    {
        if (effective)
        {
            var resolved = Resolve(new CliArguments(CliCommandKind.Run));
            return JsonSerializer.Serialize(new
            {
                agent = resolved.Agent, workflow = resolved.Workflow,
                server = resolved.Server, model = resolved.Model,
                workspace = resolved.Workspace, workspaceId = resolved.WorkspaceId,
                sandbox = resolved.Sandbox, timeout = FormatTimeout(resolved.Timeout),
                progress = resolved.Progress, inputs = resolved.Inputs,
                permissions = new {
                    read = resolved.AllowRead, write = resolved.AllowWrite,
                    shell = resolved.AllowShell, fileMemory = resolved.AllowFileMemory
                }
            }, JsonOptions);
        }
        var path = localOnly ? ProjectPath : GlobalPath;
        var root = ReadDocument(path);
        return root.ToJsonString(JsonOptions);
    }

    public void Set(string key, string value, bool local)
    {
        if (!IsConfigurableKey(key))
            throw new CliUsageException("Unknown or unsupported config key: " + key);
        if (string.IsNullOrWhiteSpace(value))
            throw new CliUsageException("Config value cannot be empty.");
        var permission = key.StartsWith("permissions.", StringComparison.OrdinalIgnoreCase);
        if (permission && !bool.TryParse(value, out _))
            throw new CliUsageException("Permission values must be true or false.");
        if (local && permission && bool.Parse(value) &&
            !Permission(Load(GlobalPath), key["permissions.".Length..], false))
            throw new CliUsageException("Project config cannot expand global permissions.");
        if (key.Equals("timeout", StringComparison.OrdinalIgnoreCase) &&
            !CliCommands.TryParseTimeout(value, out _))
            throw new CliUsageException("Invalid timeout.");
        if (key.Equals("progress", StringComparison.OrdinalIgnoreCase) &&
            !bool.TryParse(value, out _))
            throw new CliUsageException("Progress must be true or false.");
        if (key.Equals("workspaceId", StringComparison.OrdinalIgnoreCase) &&
            (!Guid.TryParse(value, out var id) || id == Guid.Empty))
            throw new CliUsageException("workspaceId must be a nonempty GUID.");

        var path = local ? ProjectPath : GlobalPath;
        var root = ReadDocument(path);
        if (permission)
        {
            if (root["permissions"] is not JsonObject permissions)
            {
                permissions = new JsonObject();
                root["permissions"] = permissions;
            }
            permissions[key["permissions.".Length..]] = bool.Parse(value);
        }
        else if (key.StartsWith("inputs.", StringComparison.OrdinalIgnoreCase))
        {
            if (root["inputs"] is not JsonObject inputs)
            {
                inputs = new JsonObject();
                root["inputs"] = inputs;
            }
            inputs[key["inputs.".Length..]] = value;
        }
        else root[key] = key.Equals("progress", StringComparison.OrdinalIgnoreCase)
            ? JsonValue.Create(bool.Parse(value)) : JsonValue.Create(value);

        if (key.Equals("agent", StringComparison.OrdinalIgnoreCase)) root.Remove("workflow");
        if (key.Equals("workflow", StringComparison.OrdinalIgnoreCase)) root.Remove("agent");
        if (key.Equals("workspace", StringComparison.OrdinalIgnoreCase)) root.Remove("workspaceId");
        if (key.Equals("workspaceId", StringComparison.OrdinalIgnoreCase)) root.Remove("workspace");
        WriteDocument(path, root);
    }

    public void Unset(string key, bool local)
    {
        if (!Settings.Contains(key) || key.Equals("inputs", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("Unknown or unsupported config key: " + key);
        var path = local ? ProjectPath : GlobalPath;
        var root = ReadDocument(path);
        if (key.StartsWith("permissions.", StringComparison.OrdinalIgnoreCase))
        {
            if (root["permissions"] is JsonObject permissions)
            {
                permissions.Remove(key["permissions.".Length..]);
                if (permissions.Count == 0) root.Remove("permissions");
            }
        }
        else if (key.StartsWith("inputs.", StringComparison.OrdinalIgnoreCase))
        {
            if (root["inputs"] is JsonObject inputs)
            {
                inputs.Remove(key["inputs.".Length..]);
                if (inputs.Count == 0) root.Remove("inputs");
            }
        }
        else root.Remove(key);
        WriteDocument(path, root);
    }

    private static bool IsConfigurableKey(string key) =>
        (Settings.Contains(key) && !key.Equals("inputs", StringComparison.OrdinalIgnoreCase)) ||
        (key.StartsWith("inputs.", StringComparison.OrdinalIgnoreCase) &&
         key.Length > "inputs.".Length &&
         !key.Contains(':'));

    private static JsonObject ReadDocument(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new CliUsageException("CLI configuration must be a JSON object: " + path);
        }
        catch (JsonException)
        {
            throw new CliUsageException("Invalid CLI JSON configuration: " + path);
        }
    }

    private static void WriteDocument(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(JsonOptions) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
