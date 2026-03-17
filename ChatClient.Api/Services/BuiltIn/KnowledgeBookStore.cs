using System.Text;
using System.Text.Json;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.BuiltIn;

public sealed class KnowledgeBookStore(
    McpServerSessionContext sessionContext)
{
    public const string KnowledgeFileParameter = "knowledgeFile";
    private const string DefaultKnowledgeFile = "UserData/knowledge-book.json";
    private const string RootOutlineReference = "0";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public KnowledgeBookContextInfo GetContext()
    {
        var storage = ResolveStorage();
        return new KnowledgeBookContextInfo(storage.KnowledgeFilePath);
    }

    public async Task<IReadOnlyList<KnowledgeBookHeadingInfo>> ListHeadingsAsync(
        string? outline,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var normalizedOutline = ParseOutlineReference(outline);
        var document = await LoadAsync(cancellationToken);
        var resolved = FindSectionAndOutline(document.Root, normalizedOutline);
        var section = resolved.Section;
        if (section is null)
        {
            throw new InvalidOperationException("section_not_found");
        }

        List<KnowledgeBookHeadingInfo> headings = [];
        var depthLimit = Math.Max(1, maxDepth);
        EnumerateHeadings(section, resolved.Outline, resolved.Path, depth: 1, depthLimit, headings);
        return headings;
    }

    public async Task<KnowledgeBookSectionSnapshot> GetSectionAsync(
        string? outline,
        CancellationToken cancellationToken)
    {
        var normalizedOutline = ParseOutlineReference(outline);
        var document = await LoadAsync(cancellationToken);
        var resolved = FindSectionAndOutline(document.Root, normalizedOutline);
        var section = resolved.Section;
        if (section is null)
        {
            throw new InvalidOperationException("section_not_found");
        }

        return CreateSnapshot(section, resolved.Path, resolved.Outline);
    }

    public async Task<KnowledgeBookSectionSnapshot> UpdateSectionAsync(
        string outline,
        string contentMarkdown,
        CancellationToken cancellationToken)
    {
        var normalizedOutline = ParseOutlineReference(outline);
        var document = await LoadAsync(cancellationToken);
        var resolved = FindSectionAndOutline(document.Root, normalizedOutline);
        var section = resolved.Section;
        if (section is null)
        {
            throw new InvalidOperationException("section_not_found");
        }

        section.ContentMarkdown = NormalizeContent(contentMarkdown);
        section.UpdatedAtUtc = DateTime.UtcNow;
        document.UpdatedAtUtc = DateTime.UtcNow;

        await SaveAsync(document, cancellationToken);
        return CreateSnapshot(section, resolved.Path, resolved.Outline);
    }

    public async Task<KnowledgeBookSectionSnapshot> InsertSectionAsync(
        string title,
        string? anchorOutline,
        bool asChild,
        string? contentMarkdown,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            throw new InvalidOperationException("title_required");
        }

        var document = await LoadAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var target = ResolveInsertTarget(document.Root, anchorOutline, asChild);
        var section = new KnowledgeBookSection
        {
            Title = normalizedTitle,
            Key = CreateUniqueKey(target.Parent.Children, normalizedTitle),
            ContentMarkdown = NormalizeContent(contentMarkdown),
            UpdatedAtUtc = now
        };

        target.Parent.Children.Insert(target.InsertIndex, section);
        target.Parent.UpdatedAtUtc = now;
        document.UpdatedAtUtc = now;

        await SaveAsync(document, cancellationToken);
        return CreateSnapshot(
            section,
            target.Path.Concat([section.Title]).ToArray(),
            target.Outline.Concat([target.InsertIndex + 1]).ToArray());
    }

    public async Task<IReadOnlyList<KnowledgeBookSearchHit>> SearchSectionsAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var document = await LoadAsync(cancellationToken);
        var terms = query
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<KnowledgeBookSearchHit> hits = [];
        Search(document.Root, [], [], terms, hits);
        return hits
            .OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.Path.Count)
            .Take(Math.Max(1, maxResults))
            .ToArray();
    }

    public async Task<string> ExportMarkdownAsync(CancellationToken cancellationToken)
    {
        var document = await LoadAsync(cancellationToken);
        StringBuilder builder = new();
        AppendMarkdown(document.Root, builder, level: 0, isRoot: true);
        return builder.ToString().Trim();
    }

    private async Task<KnowledgeBookDocument> LoadAsync(CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        if (!File.Exists(storage.KnowledgeFilePath))
        {
            return CreateEmptyDocument();
        }

        await using var stream = File.OpenRead(storage.KnowledgeFilePath);
        var document = await JsonSerializer.DeserializeAsync<KnowledgeBookDocument>(stream, JsonOptions, cancellationToken);
        return document ?? CreateEmptyDocument();
    }

    private async Task SaveAsync(KnowledgeBookDocument document, CancellationToken cancellationToken)
    {
        var storage = ResolveStorage();
        Directory.CreateDirectory(Path.GetDirectoryName(storage.KnowledgeFilePath)!);

        await using var stream = File.Create(storage.KnowledgeFilePath);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
    }

    private KnowledgeBookStorage ResolveStorage()
    {
        var binding = sessionContext.Binding;
        var configuredPath = binding?.Parameters.TryGetValue(KnowledgeFileParameter, out var knowledgeFile) == true &&
                             !string.IsNullOrWhiteSpace(knowledgeFile)
            ? knowledgeFile
            : DefaultKnowledgeFile;

        var knowledgeFilePath = ResolveAbsolutePath(configuredPath!);
        return new KnowledgeBookStorage(knowledgeFilePath);
    }

    private static string ResolveAbsolutePath(string path)
    {
        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path));
    }

    private static KnowledgeBookDocument CreateEmptyDocument()
    {
        return new KnowledgeBookDocument
        {
            Root = new KnowledgeBookSection
            {
                Key = "root",
                Title = "Knowledge Book"
            }
        };
    }

    private static List<int> ParseOutlineReference(string? outline)
    {
        if (string.IsNullOrWhiteSpace(outline))
        {
            return [];
        }

        var trimmed = outline.Trim();
        if (string.Equals(trimmed, RootOutlineReference, StringComparison.Ordinal))
        {
            return [];
        }

        var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return [];
        }

        List<int> result = [];
        foreach (var segment in segments)
        {
            if (!int.TryParse(segment, out var index) || index <= 0)
            {
                throw new InvalidOperationException("invalid_outline_reference");
            }

            result.Add(index);
        }

        return result;
    }

    private static string NormalizeTitle(string title)
        => title?.Trim() ?? string.Empty;

    private static string? NormalizeContent(string? contentMarkdown)
        => string.IsNullOrWhiteSpace(contentMarkdown) ? null : contentMarkdown.Trim();

    private static (KnowledgeBookSection? Section, IReadOnlyList<int> Outline, IReadOnlyList<string> Path) FindSectionAndOutline(
        KnowledgeBookSection root,
        IReadOnlyList<int> outline)
    {
        var current = root;
        List<string> path = [];
        foreach (var segment in outline)
        {
            var childIndex = segment - 1;
            if (childIndex < 0 || childIndex >= current.Children.Count)
            {
                return (null, [], []);
            }

            current = current.Children[childIndex];
            path.Add(current.Title);
        }

        return (current, outline, path);
    }

    private static InsertTarget ResolveInsertTarget(
        KnowledgeBookSection root,
        string? anchorOutline,
        bool asChild)
    {
        if (asChild)
        {
            if (IsVirtualLevelStartReference(anchorOutline))
            {
                throw new InvalidOperationException("virtual_anchor_requires_same_level");
            }

            var normalizedAnchor = ParseOutlineReference(anchorOutline);
            var anchorSection = FindSectionAndOutline(root, normalizedAnchor);
            if (anchorSection.Section is null)
            {
                throw new InvalidOperationException("section_not_found");
            }

            return new InsertTarget(
                anchorSection.Section,
                anchorSection.Outline,
                anchorSection.Path,
                anchorSection.Section.Children.Count);
        }

        if (string.IsNullOrWhiteSpace(anchorOutline))
        {
            return new InsertTarget(root, [], [], root.Children.Count);
        }

        var insertAnchor = ParseInsertAnchorReference(anchorOutline);
        var parent = FindSectionAndOutline(root, insertAnchor.ParentOutline);
        if (parent.Section is null)
        {
            throw new InvalidOperationException("section_not_found");
        }

        if (!insertAnchor.IsVirtualLevelStart)
        {
            var existingAnchor = FindSectionAndOutline(root, insertAnchor.AbsoluteOutline);
            if (existingAnchor.Section is null)
            {
                throw new InvalidOperationException("section_not_found");
            }
        }

        return new InsertTarget(parent.Section, parent.Outline, parent.Path, insertAnchor.InsertIndex);
    }

    private static InsertAnchorReference ParseInsertAnchorReference(string anchorOutline)
    {
        var trimmed = anchorOutline?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new InsertAnchorReference([], [], IsVirtualLevelStart: false, InsertIndex: 0);
        }

        if (string.Equals(trimmed, RootOutlineReference, StringComparison.Ordinal))
        {
            return new InsertAnchorReference([], [], IsVirtualLevelStart: true, InsertIndex: 0);
        }

        var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("invalid_insert_anchor_reference");
        }

        List<int> outline = [];
        var isVirtualLevelStart = false;
        for (var index = 0; index < segments.Length; index++)
        {
            if (!int.TryParse(segments[index], out var value))
            {
                throw new InvalidOperationException("invalid_insert_anchor_reference");
            }

            var isLast = index == segments.Length - 1;
            if (value == 0)
            {
                if (!isLast)
                {
                    throw new InvalidOperationException("invalid_insert_anchor_reference");
                }

                isVirtualLevelStart = true;
                continue;
            }

            if (value < 0)
            {
                throw new InvalidOperationException("invalid_insert_anchor_reference");
            }

            outline.Add(value);
        }

        if (outline.Count == 0 && !isVirtualLevelStart)
        {
            throw new InvalidOperationException("invalid_insert_anchor_reference");
        }

        if (isVirtualLevelStart)
        {
            return new InsertAnchorReference(outline, [], IsVirtualLevelStart: true, InsertIndex: 0);
        }

        var parentOutline = outline.Take(outline.Count - 1).ToArray();
        var insertIndex = outline[^1];
        return new InsertAnchorReference(parentOutline, outline, IsVirtualLevelStart: false, InsertIndex: insertIndex);
    }

    private static bool IsVirtualLevelStartReference(string? anchorOutline)
    {
        if (string.IsNullOrWhiteSpace(anchorOutline))
        {
            return false;
        }

        var trimmed = anchorOutline.Trim();
        return string.Equals(trimmed, RootOutlineReference, StringComparison.Ordinal) ||
               trimmed.EndsWith(".0", StringComparison.Ordinal);
    }

    private static string CreateUniqueKey(IEnumerable<KnowledgeBookSection> siblings, string title)
    {
        var baseKey = Slugify(title);
        var key = baseKey;
        var suffix = 2;

        while (siblings.Any(sibling => string.Equals(sibling.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            key = $"{baseKey}-{suffix}";
            suffix++;
        }

        return key;
    }

    private static string Slugify(string title)
    {
        Span<char> buffer = stackalloc char[title.Length];
        var length = 0;
        var lastWasDash = false;

        foreach (var character in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = character;
                lastWasDash = false;
                continue;
            }

            if (lastWasDash)
            {
                continue;
            }

            buffer[length++] = '-';
            lastWasDash = true;
        }

        var result = new string(buffer[..length]).Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "section" : result;
    }

    private static void EnumerateHeadings(
        KnowledgeBookSection parent,
        IReadOnlyList<int> outlinePrefix,
        IReadOnlyList<string> pathPrefix,
        int depth,
        int depthLimit,
        ICollection<KnowledgeBookHeadingInfo> target)
    {
        for (var index = 0; index < parent.Children.Count; index++)
        {
            var child = parent.Children[index];
            var outline = outlinePrefix.Concat([index + 1]).ToArray();
            var path = pathPrefix.Concat([child.Title]).ToArray();

            target.Add(new KnowledgeBookHeadingInfo(
                FormatOutlineReference(outline),
                child.Key,
                child.Title,
                path,
                child.Children.Count > 0,
                child.Children.Count));

            if (depth < depthLimit)
            {
                EnumerateHeadings(child, outline, path, depth + 1, depthLimit, target);
            }
        }
    }

    private static KnowledgeBookSectionSnapshot CreateSnapshot(
        KnowledgeBookSection section,
        IReadOnlyList<string> path,
        IReadOnlyList<int> outline)
    {
        return new KnowledgeBookSectionSnapshot(
            FormatOutlineReference(outline),
            section.Key,
            section.Title,
            path,
            section.ContentMarkdown ?? string.Empty,
            section.Children
                .Select((child, index) => new KnowledgeBookHeadingInfo(
                    FormatOutlineReference(outline.Concat([index + 1]).ToArray()),
                    child.Key,
                    child.Title,
                    path.Concat([child.Title]).ToArray(),
                    child.Children.Count > 0,
                    child.Children.Count))
                .ToArray(),
            section.UpdatedAtUtc);
    }

    private static void Search(
        KnowledgeBookSection section,
        IReadOnlyList<int> outline,
        IReadOnlyList<string> path,
        IReadOnlyList<string> terms,
        ICollection<KnowledgeBookSearchHit> hits)
    {
        for (var index = 0; index < section.Children.Count; index++)
        {
            var child = section.Children[index];
            var childOutline = outline.Concat([index + 1]).ToArray();
            var childPath = path.Concat([child.Title]).ToArray();
            var score = Score(child, terms);
            if (score > 0)
            {
                hits.Add(new KnowledgeBookSearchHit(
                    FormatOutlineReference(childOutline),
                    child.Key,
                    child.Title,
                    childPath,
                    score,
                    BuildSnippet(child.ContentMarkdown)));
            }

            Search(child, childOutline, childPath, terms, hits);
        }
    }

    private static string FormatOutlineReference(IReadOnlyList<int> outline)
    {
        return outline.Count == 0 ? RootOutlineReference : string.Join('.', outline);
    }

    private static int Score(KnowledgeBookSection section, IReadOnlyList<string> terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (section.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }

            if (!string.IsNullOrWhiteSpace(section.ContentMarkdown) &&
                section.ContentMarkdown.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }
        }

        return score;
    }

    private static string BuildSnippet(string? contentMarkdown)
    {
        if (string.IsNullOrWhiteSpace(contentMarkdown))
        {
            return string.Empty;
        }

        var snippet = contentMarkdown
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        return snippet.Length <= 180 ? snippet : snippet[..180] + "...";
    }

    private static void AppendMarkdown(
        KnowledgeBookSection section,
        StringBuilder builder,
        int level,
        bool isRoot)
    {
        if (!isRoot)
        {
            builder.Append('#', Math.Min(level, 6));
            builder.Append(' ');
            builder.AppendLine(section.Title);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(section.ContentMarkdown))
        {
            builder.AppendLine(section.ContentMarkdown.Trim());
            builder.AppendLine();
        }

        foreach (var child in section.Children)
        {
            AppendMarkdown(child, builder, level + 1, isRoot: false);
        }
    }

    public sealed class KnowledgeBookDocument
    {
        public int Version { get; set; } = 1;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public KnowledgeBookSection Root { get; set; } = new();
    }

    public sealed class KnowledgeBookSection
    {
        public string Key { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string? ContentMarkdown { get; set; }

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public List<KnowledgeBookSection> Children { get; set; } = [];
    }

    private sealed record KnowledgeBookStorage(string KnowledgeFilePath);
    private sealed record InsertTarget(
        KnowledgeBookSection Parent,
        IReadOnlyList<int> Outline,
        IReadOnlyList<string> Path,
        int InsertIndex);

    private sealed record InsertAnchorReference(
        IReadOnlyList<int> ParentOutline,
        IReadOnlyList<int> AbsoluteOutline,
        bool IsVirtualLevelStart,
        int InsertIndex);
}

public sealed record KnowledgeBookContextInfo(string KnowledgeFile);

public sealed record KnowledgeBookHeadingInfo(
    string Outline,
    string Key,
    string Title,
    IReadOnlyList<string> Path,
    bool HasChildren,
    int ChildCount);

public sealed record KnowledgeBookSectionSnapshot(
    string Outline,
    string Key,
    string Title,
    IReadOnlyList<string> Path,
    string ContentMarkdown,
    IReadOnlyList<KnowledgeBookHeadingInfo> Children,
    DateTime UpdatedAtUtc);

public sealed record KnowledgeBookSearchHit(
    string Outline,
    string Key,
    string Title,
    IReadOnlyList<string> Path,
    int Score,
    string Snippet);
