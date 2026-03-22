using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatClient.Api.Services.BuiltIn;

internal sealed record MarkdownDocument(
    string Id,
    string Title,
    IReadOnlyList<MarkdownDocumentItem> Items,
    string SourceText,
    MarkdownDocumentSection RootSection);

internal sealed record MarkdownDocumentItem(
    int Index,
    MarkdownDocumentItemType Type,
    string Markdown,
    string Text,
    MarkdownDocumentPointer Pointer,
    int HeadingLevel,
    int StartOffset,
    int EndOffset,
    int StartLine,
    int EndLine);

internal enum MarkdownDocumentItemType
{
    Heading,
    Paragraph,
    ListItem,
    Code,
    ThematicBreak,
    Html
}

internal enum MarkdownDocumentEditAction
{
    Replace,
    InsertBefore,
    InsertAfter,
    Remove,
    Split,
    MergeWithNext,
    MergeWithPrevious
}

internal sealed record MarkdownDocumentEditOperation(
    MarkdownDocumentEditAction Action,
    MarkdownDocumentPointer? TargetPointer,
    int? TargetIndex,
    IReadOnlyList<MarkdownDocumentItem> Items);

internal sealed class MarkdownDocumentSection
{
    public string Outline { get; set; } = MarkdownDocumentSession.RootOutlineReference;
    public string Title { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Level { get; set; }
    public int StartLine { get; set; } = 1;
    public int EndLine { get; set; } = 1;
    public int StartItemIndex { get; set; } = -1;
    public int EndItemIndex { get; set; } = -1;
    public int ContentStartItemIndex { get; set; } = -1;
    public int ContentEndItemIndex { get; set; } = -1;
    public string? ContentMarkdown { get; set; }
    public List<MarkdownDocumentSection> Children { get; } = [];
}

internal static class MarkdownDocumentItemTypeExtensions
{
    public static string ToWireName(this MarkdownDocumentItemType type) => type switch
    {
        MarkdownDocumentItemType.Heading => "heading",
        MarkdownDocumentItemType.Paragraph => "paragraph",
        MarkdownDocumentItemType.ListItem => "list_item",
        MarkdownDocumentItemType.Code => "code",
        MarkdownDocumentItemType.ThematicBreak => "thematic_break",
        MarkdownDocumentItemType.Html => "html",
        _ => "paragraph"
    };
}

internal sealed class MarkdownDocumentPointer
{
    public string Label { get; }

    public MarkdownDocumentPointer(string label)
    {
        Label = NormalizeLabel(label);
        if (string.IsNullOrWhiteSpace(Label))
        {
            throw new ArgumentException("Markdown pointer label cannot be empty.", nameof(label));
        }
    }

    [JsonIgnore]
    public PathParts Parsed => PathParts.TryParse(Label, out var path) ? path : default;

    public string ToCompactString() => Label;

    public bool BelongsTo(MarkdownDocumentPointer sectionPointer)
    {
        var container = sectionPointer.Parsed;
        var current = Parsed;

        if (container.Numbers is null || container.Numbers.Length == 0)
        {
            return false;
        }

        if (container.Paragraph is not null)
        {
            return container.Equals(current);
        }

        if (current.Numbers is null || current.Numbers.Length < container.Numbers.Length)
        {
            return false;
        }

        for (var index = 0; index < container.Numbers.Length; index++)
        {
            if (container.Numbers[index] != current.Numbers[index])
            {
                return false;
            }
        }

        return true;
    }

    public string Serialize() => JsonSerializer.Serialize(this, SerializationOptions);

    public static bool TryParse(string? raw, out MarkdownDocumentPointer? pointer)
    {
        pointer = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        string? label = null;

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (TryReadLabel(document.RootElement, out var jsonLabel))
                {
                    label = jsonLabel;
                }
            }
            catch
            {
                return false;
            }
        }
        else
        {
            label = trimmed;
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex > 0)
            {
                var prefix = trimmed[..colonIndex];
                var suffix = trimmed[(colonIndex + 1)..];
                if (IsDigits(prefix) && !string.IsNullOrWhiteSpace(suffix))
                {
                    label = suffix;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        label = NormalizeLabel(label);
        if (!PathParts.TryParse(label, out _))
        {
            return false;
        }

        pointer = new MarkdownDocumentPointer(label);
        return true;
    }

    private static bool TryReadLabel(JsonElement root, out string? label)
    {
        label = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "label", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                label = property.Value.GetString();
            }

            return !string.IsNullOrWhiteSpace(label);
        }

        return false;
    }

    private static bool IsDigits(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var normalized = label.Trim();
        normalized = normalized.Replace('P', 'p');

        var pIndex = normalized.IndexOf('p');
        if (pIndex > 0 && normalized[pIndex - 1] != '.')
        {
            normalized = normalized.Insert(pIndex, ".");
        }

        return normalized;
    }

    private static JsonSerializerOptions SerializationOptions { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public override string ToString() => Serialize();

    public readonly record struct PathParts(int[]? Numbers, int? Paragraph)
    {
        public static bool TryParse(string? label, out PathParts path)
        {
            path = default;
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            label = label.Trim();
            label = label.Replace(".p", "p", StringComparison.OrdinalIgnoreCase);

            if (label.Length >= 2 && (label[0] == 'p' || label[0] == 'P'))
            {
                if (!int.TryParse(label.AsSpan(1), out var onlyParagraph) || onlyParagraph < 0)
                {
                    return false;
                }

                path = new PathParts([], onlyParagraph);
                return true;
            }

            var parts = label.Split('p', 'P');
            if (parts.Length > 2)
            {
                return false;
            }

            var left = parts[0];
            int? paragraph = null;
            if (parts.Length == 2)
            {
                if (string.IsNullOrWhiteSpace(parts[1]) ||
                    !int.TryParse(parts[1], out var parsedParagraph) ||
                    parsedParagraph < 0)
                {
                    return false;
                }

                paragraph = parsedParagraph;
            }

            var numberSegments = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (numberSegments.Length == 0)
            {
                return false;
            }

            var numbers = new int[numberSegments.Length];
            for (var index = 0; index < numberSegments.Length; index++)
            {
                if (!int.TryParse(numberSegments[index], out var number) || number < 0)
                {
                    return false;
                }

                numbers[index] = number;
            }

            path = new PathParts(numbers, paragraph);
            return true;
        }
    }
}

public sealed record MarkdownDocumentContextInfo(
    string SourceFile,
    string Title,
    string DocumentId,
    int ItemCount);

public sealed record MarkdownDocumentHeadingInfo(
    string Outline,
    string Title,
    string Path,
    int Level,
    int ChildCount,
    bool HasContent,
    int StartLine,
    int EndLine);

public sealed record MarkdownDocumentSectionSnapshot(
    string Outline,
    string Title,
    string Path,
    int Level,
    string? ContentMarkdown,
    int StartLine,
    int EndLine,
    IReadOnlyList<MarkdownDocumentHeadingInfo> Children);

public sealed record MarkdownDocumentSearchHit(
    string Outline,
    string Title,
    string Path,
    int Level,
    string Snippet,
    int Score);

public sealed record MarkdownDocumentItemSnapshot(
    int Index,
    string Pointer,
    string Type,
    string Markdown,
    string Text,
    int StartLine,
    int EndLine,
    int HeadingLevel);

public sealed record MarkdownDocumentItemBatch(
    string Outline,
    int MaxItems,
    bool IncludeHeadings,
    string? StartAfterPointer,
    bool HasMore,
    string? NextAfterPointer,
    IReadOnlyList<MarkdownDocumentItemSnapshot> Items);

internal sealed class MarkdownDocumentCursorState
{
    public required string CursorName { get; init; }

    public required string Outline { get; set; }

    public required bool IncludeHeadings { get; set; }

    public required int BatchSize { get; set; }

    public string? InitialStartAfterPointer { get; init; }

    public string? CurrentStartAfterPointer { get; set; }

    public bool IsCompleted { get; set; }

    public int BatchesRead { get; set; }

    public int ItemsRead { get; set; }

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record MarkdownDocumentCursorSnapshot(
    string CursorName,
    string Outline,
    int BatchSize,
    bool IncludeHeadings,
    string? InitialStartAfterPointer,
    string? CurrentStartAfterPointer,
    bool HasMore,
    int BatchesRead,
    int ItemsRead,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record MarkdownDocumentCursorBatch(
    string CursorName,
    string Outline,
    int MaxItems,
    bool IncludeHeadings,
    string? StartAfterPointer,
    bool HasMore,
    string? NextAfterPointer,
    int BatchesRead,
    int ItemsRead,
    IReadOnlyList<MarkdownDocumentItemSnapshot> Items);

public sealed record MarkdownDocumentApplyOperationsResult(
    string SourceFile,
    string Title,
    string DocumentId,
    int ItemCount,
    int AppliedOperationCount);

public sealed record MarkdownDocumentDraftItemInput(
    [property: JsonPropertyName("markdown"), Description("Raw markdown for one inserted or replacement block item.")]
    string Markdown);

public sealed record MarkdownDocumentEditOperationInput(
    [property: JsonPropertyName("action"), Description("Edit action: replace, insert_before, insert_after, remove, split, merge_with_next, or merge_with_previous.")]
    string Action,
    [property: JsonPropertyName("targetPointer"), Description("Semantic pointer of the target item. Prefer this over targetIndex.")]
    string? TargetPointer = null,
    [property: JsonPropertyName("targetIndex"), Description("Optional zero-based target item index when pointer is unavailable.")]
    int? TargetIndex = null,
    [property: JsonPropertyName("items"), Description("Replacement or inserted markdown items. Each entry must describe exactly one markdown block item.")]
    IReadOnlyList<MarkdownDocumentDraftItemInput>? Items = null);

public sealed record MarkdownDocumentApplyOperationsInput(
    [property: JsonPropertyName("operations"), Description("Ordered markdown edit operations to apply to the bound source file.")]
    IReadOnlyList<MarkdownDocumentEditOperationInput> Operations);
