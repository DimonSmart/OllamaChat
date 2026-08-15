using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record DiscoveredAgentSkill(string Name, string Description, string SourcePath, AgentSkillSourceKind SourceKind);
internal sealed record AgentSkillsDiscoveryResult(
    IReadOnlyList<DiscoveredAgentSkill> Skills,
    IReadOnlyList<string> Diagnostics,
    AgentSkillsSource? Source);

internal static class AgentSkillsDiscovery
{
    private static readonly string[] Excluded = [".git", "bin", "obj", "node_modules", "packages"];
    public static async Task<AgentSkillsDiscoveryResult> DiscoverAsync(
        AgentSkillsProfile profile,
        string? workspacePath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        List<string> diagnostics = [];
        List<(string Path, AgentSkillSourceKind Kind)> directories = [];
        HashSet<string> seenDirectories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var sources = profile.FileSources.Where(x => !Path.IsPathFullyQualified(x.Directory)).Select(x => (x, AgentSkillSourceKind.WorkspaceFile))
            .Concat(profile.IncludeClaudeSkills ? [(new SkillFileSource { Directory = ".claude" + Path.DirectorySeparatorChar + "skills", Patterns = ["**/SKILL.md"] }, AgentSkillSourceKind.Claude)] : [])
            .Concat(profile.FileSources.Where(x => Path.IsPathFullyQualified(x.Directory)).Select(x => (x, AgentSkillSourceKind.InstalledFile)));
        foreach (var (source, kind) in sources)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) && kind != AgentSkillSourceKind.InstalledFile)
            { diagnostics.Add($"Ignoring skill source '{source.Directory}': a workspace is required."); continue; }
            var root = Path.GetFullPath(Path.IsPathFullyQualified(source.Directory) ? source.Directory : Path.Combine(workspacePath!, source.Directory));
            if (!Directory.Exists(root))
            { logger.LogInformation("Skill directory {Path} does not exist.", root); diagnostics.Add($"Skill directory does not exist: {root}"); continue; }
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var pattern in source.Patterns)
                matcher.AddInclude(pattern.Replace('\\', '/'));
            if (kind == AgentSkillSourceKind.WorkspaceFile)
                foreach (var excluded in Excluded)
                    matcher.AddExclude($"**/{excluded}/**");
            foreach (var match in matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(root))).Files)
            {
                var skillFilePath = Path.GetFullPath(Path.Combine(root, match.Path));
                var skillDirectory = Path.GetDirectoryName(skillFilePath)!;
                if (!IsContainedByRoot(skillDirectory, root) ||
                    !Path.GetFileName(skillFilePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seenDirectories.Add(skillDirectory))
                    directories.Add((skillDirectory, kind));
            }
        }

        if (directories.Count == 0)
            return new([], diagnostics, null);

        var skills = new List<DiscoveredAgentSkill>();
        var runtimeSources = new List<AgentSkillsSource>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new CachingAgentSkillsSource(new AgentFileSkillsSource(directory.Path));
            try
            {
                // The native source is loaded once here and its framework cache is retained for the session.
                var loaded = await source.GetSkillsAsync(null!, cancellationToken);
                if (loaded.Count == 0)
                {
                    diagnostics.Add($"Skill directory '{directory.Path}' was ignored by Agent Framework: invalid or unsupported SKILL.md.");
                    source.Dispose();
                    continue;
                }

                var selected = false;
                foreach (var skill in loaded)
                {
                    var fileSkill = skill as AgentFileSkill;
                    var sourcePath = fileSkill?.Path ?? directory.Path;
                    if (!names.Add(skill.Frontmatter.Name))
                    {
                        var loadedSkill = skills.First(x => string.Equals(x.Name, skill.Frontmatter.Name, StringComparison.OrdinalIgnoreCase));
                        diagnostics.Add($"Skill '{skill.Frontmatter.Name}' from '{sourcePath}' ignored: same name already loaded from '{loadedSkill.SourcePath}'.");
                        continue;
                    }

                    selected = true;
                    skills.Add(new DiscoveredAgentSkill(
                        skill.Frontmatter.Name,
                        skill.Frontmatter.Description,
                        sourcePath,
                        directory.Kind));
                }

                if (selected)
                    runtimeSources.Add(source);
                else
                    source.Dispose();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                source.Dispose();
                foreach (var selectedSource in runtimeSources)
                    selectedSource.Dispose();
                throw;
            }
            catch (Exception exception)
            {
                source.Dispose();
                logger.LogWarning(exception, "Agent Framework rejected skill directory {Path}.", directory.Path);
                diagnostics.Add($"Skill directory '{directory.Path}' was ignored by Agent Framework: {exception.Message}");
            }
        }

        if (skills.Count == 0)
            return new([], diagnostics, null);

        return new(skills, diagnostics, new AggregatingAgentSkillsSource(runtimeSources));
    }

    private static bool IsContainedByRoot(string directory, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedDirectory, normalizedRoot, comparison) ||
            normalizedDirectory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
