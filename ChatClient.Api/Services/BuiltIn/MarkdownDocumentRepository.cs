using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ChatClient.Api.Services.BuiltIn;

public sealed class MarkdownDocumentRepository
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().Build();

    internal MarkdownDocument LoadFromMarkdown(string markdown, string title, string? documentId = null)
    {
        var normalized = NormalizeMarkdownContent(markdown);
        var markdownDocument = Markdown.Parse(normalized, _pipeline);
        var lineMap = BuildLineMap(normalized);
        var parsingState = new PointerLabelState();
        var items = new List<MarkdownDocumentItem>();

        foreach (var block in markdownDocument)
        {
            AppendItems(block, normalized, lineMap, parsingState, items);
        }

        var reindexed = Reindex(items);
        var rootSection = BuildSections(reindexed, normalized, title, lineMap.Count);
        return new MarkdownDocument(
            documentId ?? Guid.NewGuid().ToString("N"),
            title,
            reindexed,
            normalized,
            rootSection);
    }

    internal string ComposeMarkdown(IEnumerable<MarkdownDocumentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return NormalizeMarkdownContent(string.Join("\n\n", items.Select(static item => item.Markdown)));
    }

    internal IReadOnlyList<MarkdownDocumentItem> ParseDraftItems(IReadOnlyList<MarkdownDocumentDraftItemInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var items = new List<MarkdownDocumentItem>(inputs.Count);
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Markdown))
            {
                throw new InvalidOperationException("invalid_edit_item_markdown");
            }

            var draftDocument = LoadFromMarkdown(input.Markdown, "Draft", "draft");
            if (draftDocument.Items.Count != 1)
            {
                throw new InvalidOperationException("invalid_edit_item_markdown");
            }

            items.Add(draftDocument.Items[0]);
        }

        return items;
    }

    private void AppendItems(
        Block block,
        string source,
        IReadOnlyList<int> lineMap,
        PointerLabelState pointerState,
        List<MarkdownDocumentItem> items)
    {
        switch (block)
        {
            case HeadingBlock heading:
                items.Add(CreateItem(
                    items.Count,
                    MarkdownDocumentItemType.Heading,
                    GetSourceText(block, source),
                    GetPlainText(heading.Inline),
                    pointerState.EnterHeading(heading.Level),
                    heading.Level,
                    block,
                    lineMap));
                break;

            case ParagraphBlock paragraph:
                items.Add(CreateItem(
                    items.Count,
                    MarkdownDocumentItemType.Paragraph,
                    GetSourceText(block, source),
                    GetPlainText(paragraph.Inline),
                    pointerState.NextPointer(),
                    0,
                    block,
                    lineMap));
                break;

            case ListBlock list:
                foreach (var child in list)
                {
                    AppendItems(child, source, lineMap, pointerState, items);
                }
                break;

            case ListItemBlock listItem:
                items.Add(CreateItem(
                    items.Count,
                    MarkdownDocumentItemType.ListItem,
                    GetSourceText(block, source),
                    GetListItemPlainText(listItem),
                    pointerState.NextPointer(),
                    0,
                    block,
                    lineMap));
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    AppendItems(child, source, lineMap, pointerState, items);
                }
                break;

            case CodeBlock code:
                items.Add(CreateItem(
                    items.Count,
                    MarkdownDocumentItemType.Code,
                    GetSourceText(block, source),
                    code.Lines.ToString(),
                    pointerState.NextPointer(),
                    0,
                    block,
                    lineMap));
                break;

            case ThematicBreakBlock:
                items.Add(CreateItem(
                    items.Count,
                    MarkdownDocumentItemType.ThematicBreak,
                    GetSourceText(block, source),
                    string.Empty,
                    pointerState.NextPointer(),
                    0,
                    block,
                    lineMap));
                break;

            case HtmlBlock:
                var htmlSource = GetSourceText(block, source);
                items.Add(CreateItem(
                    items.Count,
                    MarkdownDocumentItemType.Html,
                    htmlSource,
                    htmlSource,
                    pointerState.NextPointer(),
                    0,
                    block,
                    lineMap));
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    AppendItems(child, source, lineMap, pointerState, items);
                }
                break;

            default:
                var fallbackSource = GetSourceText(block, source);
                if (!string.IsNullOrWhiteSpace(fallbackSource))
                {
                    items.Add(CreateItem(
                        items.Count,
                        MarkdownDocumentItemType.Paragraph,
                        fallbackSource,
                        fallbackSource,
                        pointerState.NextPointer(),
                        0,
                        block,
                        lineMap));
                }
                break;
        }
    }

    private static MarkdownDocumentItem CreateItem(
        int index,
        MarkdownDocumentItemType type,
        string markdown,
        string text,
        string pointerLabel,
        int headingLevel,
        Block block,
        IReadOnlyList<int> lineMap)
    {
        var startOffset = Math.Max(0, block.Span.Start);
        var endOffset = Math.Max(startOffset, block.Span.End);
        return new MarkdownDocumentItem(
            index,
            type,
            markdown,
            text,
            new MarkdownDocumentPointer(pointerLabel),
            headingLevel,
            startOffset,
            endOffset,
            ResolveLineNumber(lineMap, startOffset),
            ResolveLineNumber(lineMap, endOffset));
    }

    private static IReadOnlyList<MarkdownDocumentItem> Reindex(IReadOnlyList<MarkdownDocumentItem> items)
    {
        var result = new List<MarkdownDocumentItem>(items.Count);
        var pointerState = new PointerLabelState();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var pointerLabel = item.Type == MarkdownDocumentItemType.Heading
                ? pointerState.EnterHeading(ResolveHeadingLevel(item))
                : pointerState.NextPointer();

            result.Add(item with
            {
                Index = index,
                Pointer = new MarkdownDocumentPointer(pointerLabel)
            });
        }

        return result;
    }

    private static int ResolveHeadingLevel(MarkdownDocumentItem item)
    {
        if (item.Type != MarkdownDocumentItemType.Heading)
        {
            return 1;
        }

        if (item.HeadingLevel > 0)
        {
            return item.HeadingLevel;
        }

        if (string.IsNullOrWhiteSpace(item.Markdown))
        {
            return 1;
        }

        var trimmed = item.Markdown.AsSpan().TrimStart();
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level > 0)
        {
            return level;
        }

        var lines = item.Markdown.Split('\n');
        if (lines.Length >= 2)
        {
            var underline = lines[1].Trim();
            if (underline.Length > 0 && underline.All(static ch => ch == '='))
            {
                return 1;
            }

            if (underline.Length > 0 && underline.All(static ch => ch == '-'))
            {
                return 2;
            }
        }

        return 1;
    }

    private static MarkdownDocumentSection BuildSections(
        IReadOnlyList<MarkdownDocumentItem> items,
        string source,
        string title,
        int totalLineCount)
    {
        var root = new MarkdownDocumentSection
        {
            Title = title,
            Path = title,
            Level = 0,
            StartLine = 1,
            EndLine = Math.Max(1, totalLineCount),
            StartItemIndex = items.Count > 0 ? 0 : -1,
            EndItemIndex = items.Count - 1
        };

        var headingItems = items
            .Where(static item => item.Type == MarkdownDocumentItemType.Heading)
            .ToList();

        if (headingItems.Count == 0)
        {
            root.ContentStartItemIndex = items.Count > 0 ? 0 : -1;
            root.ContentEndItemIndex = items.Count - 1;
            root.ContentMarkdown = ExtractContentMarkdown(items, source, root.ContentStartItemIndex, root.ContentEndItemIndex);
            return root;
        }

        var stack = new Stack<MarkdownDocumentSection>();
        var orderedSections = new List<MarkdownDocumentSection>(headingItems.Count);
        stack.Push(root);

        foreach (var heading in headingItems)
        {
            while (stack.Count > 1 && stack.Peek().Level >= heading.HeadingLevel)
            {
                stack.Pop();
            }

            var parent = stack.Peek();
            var section = new MarkdownDocumentSection
            {
                Outline = heading.Pointer.ToCompactString(),
                Title = string.IsNullOrWhiteSpace(heading.Text) ? heading.Pointer.ToCompactString() : heading.Text,
                Path = string.IsNullOrWhiteSpace(parent.Path) ? heading.Text : $"{parent.Path} > {heading.Text}",
                Level = heading.HeadingLevel,
                StartLine = heading.StartLine,
                StartItemIndex = heading.Index
            };

            parent.Children.Add(section);
            orderedSections.Add(section);
            stack.Push(section);
        }

        root.ContentStartItemIndex = headingItems[0].Index > 0 ? 0 : -1;
        root.ContentEndItemIndex = headingItems[0].Index - 1;
        root.ContentMarkdown = ExtractContentMarkdown(items, source, root.ContentStartItemIndex, root.ContentEndItemIndex);

        for (var index = 0; index < orderedSections.Count; index++)
        {
            var section = orderedSections[index];
            var nextSameOrHigher = FindNextSameOrHigherSection(orderedSections, index);
            var nextSameOrHigherStartIndex = nextSameOrHigher?.StartItemIndex ?? items.Count;
            var nextSameOrHigherStartLine = nextSameOrHigher?.StartLine ?? Math.Max(1, totalLineCount + 1);
            var firstChildStartIndex = section.Children.Count > 0 ? section.Children[0].StartItemIndex : nextSameOrHigherStartIndex;

            section.EndItemIndex = nextSameOrHigherStartIndex - 1;
            section.EndLine = Math.Max(section.StartLine, nextSameOrHigherStartLine - 1);
            section.ContentStartItemIndex = section.StartItemIndex + 1;
            section.ContentEndItemIndex = firstChildStartIndex - 1;
            section.ContentMarkdown = ExtractContentMarkdown(items, source, section.ContentStartItemIndex, section.ContentEndItemIndex);
        }

        return root;
    }

    private static MarkdownDocumentSection? FindNextSameOrHigherSection(
        IReadOnlyList<MarkdownDocumentSection> sections,
        int currentIndex)
    {
        var current = sections[currentIndex];
        for (var index = currentIndex + 1; index < sections.Count; index++)
        {
            if (sections[index].Level <= current.Level)
            {
                return sections[index];
            }
        }

        return null;
    }

    private static string? ExtractContentMarkdown(
        IReadOnlyList<MarkdownDocumentItem> items,
        string source,
        int startItemIndex,
        int endItemIndex)
    {
        if (startItemIndex < 0 || endItemIndex < startItemIndex || startItemIndex >= items.Count)
        {
            return null;
        }

        endItemIndex = Math.Min(endItemIndex, items.Count - 1);
        var startOffset = items[startItemIndex].StartOffset;
        var endOffset = items[endItemIndex].EndOffset;
        if (endOffset < startOffset || startOffset < 0 || startOffset >= source.Length)
        {
            return null;
        }

        var length = Math.Min(source.Length - startOffset, endOffset - startOffset + 1);
        if (length <= 0)
        {
            return null;
        }

        var content = source.Substring(startOffset, length).Trim();
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    private static List<int> BuildLineMap(string source)
    {
        List<int> lineStarts = [0];
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\n')
            {
                lineStarts.Add(index + 1);
            }
        }

        return lineStarts;
    }

    private static int ResolveLineNumber(IReadOnlyList<int> lineMap, int offset)
    {
        if (lineMap.Count == 0)
        {
            return 1;
        }

        var boundedOffset = Math.Max(0, offset);
        var low = 0;
        var high = lineMap.Count - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (lineMap[mid] == boundedOffset)
            {
                return mid + 1;
            }

            if (lineMap[mid] < boundedOffset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return Math.Max(1, high + 1);
    }

    private static string GetSourceText(Block block, string source)
    {
        if (block.Span.IsEmpty || source.Length == 0)
        {
            return string.Empty;
        }

        var start = Math.Max(0, block.Span.Start);
        var end = Math.Min(source.Length - 1, Math.Max(start, block.Span.End));
        var length = end - start + 1;
        return length > 0 ? source.Substring(start, length) : string.Empty;
    }

    private static string GetPlainText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content);
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case ContainerInline container:
                    builder.Append(GetPlainText(container));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string GetListItemPlainText(ListItemBlock listItem)
    {
        var paragraph = listItem.OfType<ParagraphBlock>().FirstOrDefault();
        if (paragraph is not null)
        {
            return GetPlainText(paragraph.Inline);
        }

        StringBuilder builder = new();
        foreach (var child in listItem)
        {
            if (child is not ContainerBlock container)
            {
                continue;
            }

            foreach (var nested in container)
            {
                if (nested is ParagraphBlock nestedParagraph)
                {
                    builder.AppendLine(GetPlainText(nestedParagraph.Inline));
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeMarkdownContent(string content) =>
        (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    private sealed class PointerLabelState
    {
        private readonly List<int> _headingCounters = [];
        private int _paragraphCounter;

        public string EnterHeading(int headingLevel)
        {
            UpdateHeadingCounters(headingLevel);
            _paragraphCounter = 0;
            return string.Join('.', _headingCounters);
        }

        public string NextPointer()
        {
            _paragraphCounter++;
            var prefix = _headingCounters.Count > 0 ? string.Join('.', _headingCounters) + "." : string.Empty;
            return $"{prefix}p{_paragraphCounter}";
        }

        private void UpdateHeadingCounters(int headingLevel)
        {
            if (headingLevel <= 0)
            {
                _headingCounters.Clear();
                _headingCounters.Add(1);
                return;
            }

            while (_headingCounters.Count < headingLevel)
            {
                _headingCounters.Add(0);
            }

            _headingCounters[headingLevel - 1]++;
            for (var index = headingLevel; index < _headingCounters.Count; index++)
            {
                _headingCounters[index] = 0;
            }

            for (var index = _headingCounters.Count - 1; index >= 0; index--)
            {
                if (_headingCounters[index] != 0)
                {
                    break;
                }

                _headingCounters.RemoveAt(index);
            }
        }
    }
}
