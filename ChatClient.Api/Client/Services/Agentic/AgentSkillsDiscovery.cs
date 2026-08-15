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
    public static AgentSkillsDiscoveryResult Discover(AgentSkillsProfile profile, string? workspacePath, ILogger logger)
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
                if (!skillDirectory.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    !Path.GetFileName(skillFilePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seenDirectories.Add(skillDirectory))
                    directories.Add((skillDirectory, kind));
            }
        }

        if (directories.Count == 0)
            return new([], diagnostics, null);

        var skills = new List<DiscoveredAgentSkill>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in directories)
            {
                using var fileSource = new AgentFileSkillsSource(directory.Path);
                // The framework owns SKILL.md parsing and preserves all files in the skill directory.
                var loaded = fileSource.GetSkillsAsync(null!, CancellationToken.None).GetAwaiter().GetResult();
                foreach (var skill in loaded.Where(skill => names.Add(skill.Frontmatter.Name)))
                {
                    var fileSkill = skill as AgentFileSkill;
                    skills.Add(new DiscoveredAgentSkill(
                        skill.Frontmatter.Name,
                        skill.Frontmatter.Description,
                        fileSkill?.Path ?? directory.Path,
                        directory.Kind));
                }
            }
            if (skills.Count == 0)
                return new([], diagnostics, null);
            if (directories.Count > skills.Count)
                diagnostics.Add($"{directories.Count - skills.Count} duplicate skill directory or directories were ignored using first configured source precedence.");
            var effectiveSource = new AggregatingAgentSkillsSource(skills.Select(skill => new AgentFileSkillsSource(skill.SourcePath)));
            return new(skills, diagnostics, effectiveSource);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Native Agent Skills discovery failed.");
            diagnostics.Add($"Native Agent Skills discovery failed: {exception.Message}");
            return new([], diagnostics, null);
        }
    }
}
