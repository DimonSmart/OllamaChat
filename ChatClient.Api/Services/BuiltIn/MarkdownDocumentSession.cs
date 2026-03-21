using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.BuiltIn;

public sealed class MarkdownDocumentSession
{
    public const string SourceFileParameter = "sourceFile";
    public const string RootOutlineReference = "0";

    private readonly McpServerSessionContext _sessionContext;
    private readonly MarkdownDocumentRepository _repository;
    private readonly MarkdownDocumentEditor _editor;

    public MarkdownDocumentSession(
        McpServerSessionContext sessionContext,
        MarkdownDocumentRepository repository,
        MarkdownDocumentEditor editor)
    {
        _sessionContext = sessionContext;
        _repository = repository;
        _editor = editor;
    }

    public MarkdownDocumentContextInfo GetContext()
    {
        var storage = ResolveStorage();
        var document = Load(storage);
        return new MarkdownDocumentContextInfo(storage.SourceFilePath, storage.DocumentTitle, document.Id, document.Items.Count);
    }

    public Task<IReadOnlyList<MarkdownDocumentHeadingInfo>> ListHeadingsAsync(
        string? outline,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedOutline = ParseOutlineReference(outline);
        var document = Load();
        var section = FindSection(document.RootSection, normalizedOutline);
        if (section is null)
        {
            throw new InvalidOperationException("section_not_found");
        }

        List<MarkdownDocumentHeadingInfo> headings = [];
        EnumerateHeadings(section, depth: 1, depthLimit: Math.Max(1, maxDepth), headings);
        return Task.FromResult<IReadOnlyList<MarkdownDocumentHeadingInfo>>(headings);
    }

    public Task<MarkdownDocumentSectionSnapshot> GetSectionAsync(
        string? outline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedOutline = ParseOutlineReference(outline);
        var document = Load();
        var section = FindSection(document.RootSection, normalizedOutline);
        if (section is null)
        {
            throw new InvalidOperationException("section_not_found");
        }

        return Task.FromResult(CreateSectionSnapshot(section));
    }

    public Task<IReadOnlyList<MarkdownDocumentSearchHit>> SearchSectionsAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<MarkdownDocumentSearchHit>>([]);
        }

        var document = Load();
        var terms = query
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<MarkdownDocumentSearchHit> hits = [];
        Search(document.RootSection, terms, hits);
        return Task.FromResult<IReadOnlyList<MarkdownDocumentSearchHit>>(hits
            .OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.Level)
            .ThenBy(static hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 100))
            .ToArray());
    }

    public Task<MarkdownDocumentItemBatch> ListItemsAsync(
        string? outline,
        string? startAfterPointer,
        int maxItems,
        bool includeHeadings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedOutline = ParseOutlineReference(outline);
        var document = Load();
        var section = FindSection(document.RootSection, normalizedOutline);
        if (section is null)
        {
            throw new InvalidOperationException("section_not_found");
        }

        var boundedMaxItems = Math.Clamp(maxItems, 1, 100);
        var startRange = Math.Max(0, section.StartItemIndex);
        var endRange = section.EndItemIndex >= section.StartItemIndex
            ? Math.Min(document.Items.Count - 1, section.EndItemIndex)
            : document.Items.Count - 1;
        var scopedItems = document.Items
            .Where(item => item.Index >= startRange && item.Index <= endRange)
            .ToList();

        var startIndex = 0;
        var normalizedStartPointer = NormalizePointerOrThrow(startAfterPointer);
        if (!string.IsNullOrWhiteSpace(normalizedStartPointer))
        {
            startIndex = scopedItems.FindIndex(item =>
                string.Equals(item.Pointer.ToCompactString(), normalizedStartPointer, StringComparison.Ordinal));
            if (startIndex < 0)
            {
                throw new InvalidOperationException("pointer_not_found");
            }

            startIndex++;
        }

        var filteredItems = scopedItems
            .Skip(startIndex)
            .Where(item => includeHeadings || item.Type != MarkdownDocumentItemType.Heading)
            .ToList();

        var selectedItems = filteredItems
            .Take(boundedMaxItems)
            .Select(CreateItemSnapshot)
            .ToArray();

        var hasMore = filteredItems.Count > boundedMaxItems;
        var nextAfterPointer = selectedItems.Length > 0 ? selectedItems[^1].Pointer : normalizedStartPointer;
        return Task.FromResult(new MarkdownDocumentItemBatch(
            Outline: normalizedOutline.Count == 0 ? RootOutlineReference : string.Join('.', normalizedOutline),
            MaxItems: boundedMaxItems,
            IncludeHeadings: includeHeadings,
            StartAfterPointer: normalizedStartPointer,
            HasMore: hasMore,
            NextAfterPointer: hasMore ? nextAfterPointer : null,
            Items: selectedItems));
    }

    public Task<MarkdownDocumentApplyOperationsResult> ApplyOperationsAsync(
        MarkdownDocumentApplyOperationsInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);

        if (input.Operations is null || input.Operations.Count == 0)
        {
            throw new InvalidOperationException("edit_operations_required");
        }

        var storage = ResolveStorage();
        var document = Load(storage);
        var operations = input.Operations.Select(ConvertOperation).ToArray();
        var updatedDocument = _editor.Apply(document, operations);
        File.WriteAllText(storage.SourceFilePath, updatedDocument.SourceText);

        return Task.FromResult(new MarkdownDocumentApplyOperationsResult(
            storage.SourceFilePath,
            updatedDocument.Title,
            updatedDocument.Id,
            updatedDocument.Items.Count,
            operations.Length));
    }

    public Task<string> ExportMarkdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Load().SourceText);
    }

    private MarkdownDocumentEditOperation ConvertOperation(MarkdownDocumentEditOperationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!TryParseAction(input.Action, out var action))
        {
            throw new InvalidOperationException("invalid_edit_action");
        }

        MarkdownDocumentPointer? targetPointer = null;
        if (!string.IsNullOrWhiteSpace(input.TargetPointer))
        {
            if (!MarkdownDocumentPointer.TryParse(input.TargetPointer, out targetPointer) || targetPointer is null)
            {
                throw new InvalidOperationException("invalid_pointer");
            }
        }

        var items = input.Items is { Count: > 0 }
            ? _repository.ParseDraftItems(input.Items)
            : [];

        return new MarkdownDocumentEditOperation(
            action,
            targetPointer,
            input.TargetIndex,
            items);
    }

    private static bool TryParseAction(string? raw, out MarkdownDocumentEditAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        action = normalized switch
        {
            "replace" => MarkdownDocumentEditAction.Replace,
            "insert_before" => MarkdownDocumentEditAction.InsertBefore,
            "insert_after" => MarkdownDocumentEditAction.InsertAfter,
            "remove" => MarkdownDocumentEditAction.Remove,
            "split" => MarkdownDocumentEditAction.Split,
            "merge_with_next" => MarkdownDocumentEditAction.MergeWithNext,
            "merge_with_previous" => MarkdownDocumentEditAction.MergeWithPrevious,
            _ => default
        };

        return normalized is
            "replace" or
            "insert_before" or
            "insert_after" or
            "remove" or
            "split" or
            "merge_with_next" or
            "merge_with_previous";
    }

    private MarkdownDocument Load()
    {
        var storage = ResolveStorage();
        return Load(storage);
    }

    private MarkdownDocument Load(MarkdownDocumentStorage storage)
    {
        if (!File.Exists(storage.SourceFilePath))
        {
            throw new InvalidOperationException("source_file_not_found");
        }

        var markdown = File.ReadAllText(storage.SourceFilePath);
        return _repository.LoadFromMarkdown(markdown, storage.DocumentTitle, storage.DocumentId);
    }

    private MarkdownDocumentStorage ResolveStorage()
    {
        var binding = _sessionContext.Binding;
        var configuredPath = binding?.Parameters.TryGetValue(SourceFileParameter, out var sourceFile) == true &&
                             !string.IsNullOrWhiteSpace(sourceFile)
            ? sourceFile
            : null;

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("source_file_required");
        }

        var sourceFilePath = ResolveAbsolutePath(configuredPath!);
        var documentTitle = Path.GetFileNameWithoutExtension(sourceFilePath);
        documentTitle = string.IsNullOrWhiteSpace(documentTitle) ? "Markdown Document" : documentTitle;
        return new MarkdownDocumentStorage(
            sourceFilePath,
            documentTitle,
            DocumentId: $"md:{documentTitle.ToLowerInvariant()}");
    }

    private static string ResolveAbsolutePath(string path)
    {
        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string? NormalizePointerOrThrow(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer))
        {
            return null;
        }

        if (!MarkdownDocumentPointer.TryParse(pointer, out var parsed) || parsed is null)
        {
            throw new InvalidOperationException("invalid_pointer");
        }

        return parsed.ToCompactString();
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

    private static MarkdownDocumentSection? FindSection(MarkdownDocumentSection root, IReadOnlyList<int> outline)
    {
        var current = root;
        foreach (var segment in outline)
        {
            var childIndex = segment - 1;
            if (childIndex < 0 || childIndex >= current.Children.Count)
            {
                return null;
            }

            current = current.Children[childIndex];
        }

        return current;
    }

    private static void EnumerateHeadings(
        MarkdownDocumentSection parent,
        int depth,
        int depthLimit,
        ICollection<MarkdownDocumentHeadingInfo> target)
    {
        foreach (var child in parent.Children)
        {
            target.Add(CreateHeadingInfo(child));
            if (depth < depthLimit)
            {
                EnumerateHeadings(child, depth + 1, depthLimit, target);
            }
        }
    }

    private static MarkdownDocumentSectionSnapshot CreateSectionSnapshot(MarkdownDocumentSection section)
    {
        return new MarkdownDocumentSectionSnapshot(
            Outline: section.Outline,
            Title: section.Title,
            Path: section.Path,
            Level: section.Level,
            ContentMarkdown: section.ContentMarkdown,
            StartLine: section.StartLine,
            EndLine: section.EndLine,
            Children: section.Children.Select(CreateHeadingInfo).ToArray());
    }

    private static MarkdownDocumentHeadingInfo CreateHeadingInfo(MarkdownDocumentSection section)
    {
        return new MarkdownDocumentHeadingInfo(
            Outline: section.Outline,
            Title: section.Title,
            Path: section.Path,
            Level: section.Level,
            ChildCount: section.Children.Count,
            HasContent: !string.IsNullOrWhiteSpace(section.ContentMarkdown),
            StartLine: section.StartLine,
            EndLine: section.EndLine);
    }

    private static MarkdownDocumentItemSnapshot CreateItemSnapshot(MarkdownDocumentItem item)
    {
        return new MarkdownDocumentItemSnapshot(
            Index: item.Index,
            Pointer: item.Pointer.ToCompactString(),
            Type: item.Type.ToWireName(),
            Markdown: item.Markdown,
            Text: item.Text,
            StartLine: item.StartLine,
            EndLine: item.EndLine,
            HeadingLevel: item.HeadingLevel);
    }

    private static void Search(
        MarkdownDocumentSection parent,
        IReadOnlyList<string> terms,
        ICollection<MarkdownDocumentSearchHit> target)
    {
        foreach (var child in parent.Children)
        {
            var score = CalculateScore(child, terms);
            if (score > 0)
            {
                target.Add(new MarkdownDocumentSearchHit(
                    Outline: child.Outline,
                    Title: child.Title,
                    Path: child.Path,
                    Level: child.Level,
                    Snippet: BuildSnippet(child),
                    Score: score));
            }

            Search(child, terms, target);
        }
    }

    private static int CalculateScore(MarkdownDocumentSection section, IReadOnlyList<string> terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (section.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (!string.IsNullOrWhiteSpace(section.ContentMarkdown) &&
                section.ContentMarkdown.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    private static string BuildSnippet(MarkdownDocumentSection section)
    {
        if (string.IsNullOrWhiteSpace(section.ContentMarkdown))
        {
            return section.Title;
        }

        var text = section.ContentMarkdown
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
        return text.Length <= 160 ? text : $"{text[..157]}...";
    }

    private sealed record MarkdownDocumentStorage(string SourceFilePath, string DocumentTitle, string DocumentId);
}
