using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record DiscoveredAgentSkill(string Name, string Description, string SourcePath, AgentSkillSourceKind SourceKind, string Content);
internal sealed record AgentSkillsDiscoveryResult(IReadOnlyList<DiscoveredAgentSkill> Skills, IReadOnlyList<string> Diagnostics)
{
    public AgentSkillsSource? Source => Skills.Count == 0 ? null : new AgentInMemorySkillsSource(Skills.Select(skill =>
        new AgentInlineSkill(new AgentSkillFrontmatter(skill.Name, skill.Description, null), skill.Content)));
}

internal static class AgentSkillsDiscovery
{
    private static readonly string[] Excluded = [".git", "bin", "obj", "node_modules", "packages"];
    public static AgentSkillsDiscoveryResult Discover(AgentSkillsProfile profile, string? workspacePath, ILogger logger)
    {
        List<DiscoveredAgentSkill> found = [];
        List<string> diagnostics = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
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
                var path = Path.GetFullPath(Path.Combine(root, match.Path));
                if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    continue;
                if (!TryRead(path, kind, out var skill, out var reason))
                { logger.LogWarning("Ignoring skill file {Path}: {Reason}", path, reason); diagnostics.Add($"Ignoring skill file {path}: {reason}"); continue; }
                if (!names.Add(skill.Name))
                { diagnostics.Add($"Skill '{skill.Name}' from '{path}' was ignored because a skill with the same name was already loaded."); continue; }
                found.Add(skill);
            }
        }
        return new(found, diagnostics);
    }
    private static bool TryRead(string path, AgentSkillSourceKind kind, out DiscoveredAgentSkill skill, out string reason)
    {
        skill = default!;
        reason = string.Empty;
        string text;
        try
        { text = File.ReadAllText(path); }
        catch (Exception ex) { reason = ex.Message; return false; }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 5 || lines[0].Trim() != "---")
        { reason = "YAML frontmatter is required."; return false; }
        var end = Array.FindIndex(lines, 1, x => x.Trim() == "---");
        if (end < 0)
        { reason = "YAML frontmatter is not terminated."; return false; }
        string? name = lines.Skip(1).Take(end - 1).Select(x => x.Split(':', 2)).Where(x => x.Length == 2 && x[0].Trim().Equals("name", StringComparison.OrdinalIgnoreCase)).Select(x => x[1].Trim().Trim('"', '\'')).FirstOrDefault();
        string? description = lines.Skip(1).Take(end - 1).Select(x => x.Split(':', 2)).Where(x => x.Length == 2 && x[0].Trim().Equals("description", StringComparison.OrdinalIgnoreCase)).Select(x => x[1].Trim().Trim('"', '\'')).FirstOrDefault();
        var body = string.Join('\n', lines.Skip(end + 1)).Trim();
        if (string.IsNullOrWhiteSpace(name))
        { reason = "name is required."; return false; }
        if (string.IsNullOrWhiteSpace(description))
        { reason = "description is required."; return false; }
        if (string.IsNullOrWhiteSpace(body))
        { reason = "instructions are required."; return false; }
        skill = new(name, description, path, kind, body);
        return true;
    }
}
