using System.Text.Json;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using ChatClient.Infrastructure.Repositories;

namespace ChatClient.Api.Services;

/// <summary>Persistent references to local Codex Markdown plugins, not a plugin installer.</summary>
public sealed class LocalMarkdownPluginService : ILocalMarkdownPluginService
{
    private readonly JsonFileRepository<List<LocalMarkdownPlugin>> repository;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public LocalMarkdownPluginService(IConfiguration configuration, ILogger<LocalMarkdownPluginService> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(configuration,
            configuration["LocalMarkdownPlugins:FilePath"], FilePathConstants.DefaultLocalMarkdownPluginsFile);
        repository = new JsonFileRepository<List<LocalMarkdownPlugin>>(path, logger, atomicWrites: true);
    }

    public async Task<IReadOnlyCollection<LocalMarkdownPlugin>> GetAllAsync() =>
        await repository.ReadAsync() ?? [];

    public async Task<LocalMarkdownPlugin> RegisterAsync(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Plugin directory is required.", nameof(directory));
        var fullPath = Path.GetFullPath(directory.Trim().Trim('"'));
        var candidate = new LocalMarkdownPlugin { Directory = fullPath };
        var inspection = Inspect(candidate);
        if (inspection.Diagnostics.Count > 0)
            throw new ArgumentException(string.Join(" ", inspection.Diagnostics), nameof(directory));

        await writeGate.WaitAsync();
        try
        {
            var all = (await repository.ReadAsync() ?? []).ToList();
            if (all.Any(x => string.Equals(x.Directory, fullPath, PathComparison)))
                throw new ArgumentException($"Plugin directory already registered: {fullPath}", nameof(directory));
            all.Add(candidate);
            await repository.WriteAsync(all);
            return candidate;
        }
        finally { writeGate.Release(); }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled)
    {
        await writeGate.WaitAsync();
        try
        {
            var all = (await repository.ReadAsync() ?? []).ToList();
            var plugin = all.FirstOrDefault(x => x.Id == id)
                ?? throw new KeyNotFoundException($"Plugin '{id}' is not registered.");
            plugin.Enabled = enabled;
            await repository.WriteAsync(all);
        }
        finally { writeGate.Release(); }
    }

    public async Task RemoveAsync(Guid id)
    {
        await writeGate.WaitAsync();
        try
        {
            var all = (await repository.ReadAsync() ?? []).ToList();
            if (all.RemoveAll(x => x.Id == id) == 0)
                throw new KeyNotFoundException($"Plugin '{id}' is not registered.");
            await repository.WriteAsync(all);
        }
        finally { writeGate.Release(); }
    }

    public LocalMarkdownPluginInspection Inspect(LocalMarkdownPlugin plugin)
    {
        var errors = new List<string>();
        var name = string.Empty;
        var version = string.Empty;
        var description = string.Empty;
        string? skillsDirectory = null;
        IReadOnlyList<string> skills = [];

        try
        {
            var root = Path.GetFullPath(plugin.Directory);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Plugin directory does not exist: {root}");
            var manifestPath = Path.Combine(root, ".codex-plugin", "plugin.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException($"Plugin manifest is missing: {manifestPath}");

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var manifest = document.RootElement;
            if (manifest.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Plugin manifest must be a JSON object.");
            name = RequiredString(manifest, "name");
            version = RequiredString(manifest, "version");
            description = RequiredString(manifest, "description");
            var skillsPath = RequiredString(manifest, "skills");

            if (Path.IsPathRooted(skillsPath))
                throw new InvalidDataException("Plugin skills path must be relative to the plugin directory.");
            var resolved = Path.GetFullPath(Path.Combine(root, skillsPath));
            var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
            if (!resolved.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("Plugin skills path leaves the plugin directory.");
            if (!Directory.Exists(resolved))
                throw new DirectoryNotFoundException($"Plugin skills directory does not exist: {resolved}");

            skillsDirectory = resolved;
            skills = Directory.EnumerateFiles(resolved, "SKILL.md", SearchOption.AllDirectories)
                .Select(path => Path.GetFileName(Path.GetDirectoryName(path))!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (skills.Count == 0)
                errors.Add($"No SKILL.md files found in {resolved}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or InvalidDataException or ArgumentException)
        {
            errors.Add(ex.Message);
            skillsDirectory = null;
        }

        return new LocalMarkdownPluginInspection(
            plugin.Id, plugin.Directory, plugin.Enabled, name, version, description,
            skillsDirectory, skills, errors);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Plugin manifest requires a nonempty '{property}' string.");
        return value.GetString()!.Trim();
    }
}
