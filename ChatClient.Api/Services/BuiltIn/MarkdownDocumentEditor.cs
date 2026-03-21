namespace ChatClient.Api.Services.BuiltIn;

public sealed class MarkdownDocumentEditor
{
    private readonly MarkdownDocumentRepository _repository;

    public MarkdownDocumentEditor(MarkdownDocumentRepository repository)
    {
        _repository = repository;
    }

    internal MarkdownDocument Apply(MarkdownDocument document, IEnumerable<MarkdownDocumentEditOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operations);

        var items = document.Items.ToList();
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            var targetIndex = ResolveIndex(items, operation);

            switch (operation.Action)
            {
                case MarkdownDocumentEditAction.Replace:
                case MarkdownDocumentEditAction.Split:
                    EnsureItemsRequired(operation);
                    ReplaceRange(items, targetIndex, 1, operation.Items);
                    break;

                case MarkdownDocumentEditAction.InsertBefore:
                    EnsureItemsRequired(operation);
                    ReplaceRange(items, targetIndex, 0, operation.Items);
                    break;

                case MarkdownDocumentEditAction.InsertAfter:
                    EnsureItemsRequired(operation);
                    ReplaceRange(items, targetIndex + 1, 0, operation.Items);
                    break;

                case MarkdownDocumentEditAction.Remove:
                    items.RemoveAt(targetIndex);
                    break;

                case MarkdownDocumentEditAction.MergeWithNext:
                    MergeWithNeighbor(items, targetIndex, 1, operation.Items);
                    break;

                case MarkdownDocumentEditAction.MergeWithPrevious:
                    MergeWithNeighbor(items, targetIndex, -1, operation.Items);
                    break;
            }
        }

        var sourceText = _repository.ComposeMarkdown(items);
        return _repository.LoadFromMarkdown(sourceText, document.Title, document.Id);
    }

    private static void EnsureItemsRequired(MarkdownDocumentEditOperation operation)
    {
        if (operation.Items.Count == 0)
        {
            throw new InvalidOperationException("edit_items_required");
        }
    }

    private static int ResolveIndex(List<MarkdownDocumentItem> items, MarkdownDocumentEditOperation operation)
    {
        if (operation.TargetPointer is not null)
        {
            var targetLabel = operation.TargetPointer.ToCompactString();
            var index = -1;
            for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                if (string.Equals(items[itemIndex].Pointer.ToCompactString(), targetLabel, StringComparison.Ordinal))
                {
                    index = itemIndex;
                    break;
                }
            }

            if (index < 0)
            {
                throw new InvalidOperationException("pointer_not_found");
            }

            return index;
        }

        if (operation.TargetIndex is int targetIndex &&
            targetIndex >= 0 &&
            targetIndex < items.Count)
        {
            return targetIndex;
        }

        if (operation.TargetIndex.HasValue)
        {
            throw new InvalidOperationException("target_index_out_of_range");
        }

        throw new InvalidOperationException("edit_target_required");
    }

    private static void MergeWithNeighbor(
        List<MarkdownDocumentItem> items,
        int targetIndex,
        int neighborOffset,
        IReadOnlyList<MarkdownDocumentItem> replacements)
    {
        var neighborIndex = targetIndex + neighborOffset;
        if (neighborIndex < 0 || neighborIndex >= items.Count)
        {
            throw new InvalidOperationException("merge_target_missing");
        }

        var mergedItem = replacements.FirstOrDefault() ?? Merge(items[targetIndex], items[neighborIndex]);
        var firstIndex = Math.Min(targetIndex, neighborIndex);
        ReplaceRange(items, firstIndex, 2, [mergedItem]);
    }

    private static MarkdownDocumentItem Merge(MarkdownDocumentItem first, MarkdownDocumentItem second)
    {
        var mergedMarkdown = string.Join(
            "\n\n",
            new[] { first.Markdown, second.Markdown }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        var mergedText = string.Join(
            "\n\n",
            new[] { first.Text, second.Text }.Where(static value => !string.IsNullOrWhiteSpace(value)));

        return first with
        {
            Markdown = mergedMarkdown,
            Text = mergedText
        };
    }

    private static void ReplaceRange(
        List<MarkdownDocumentItem> items,
        int startIndex,
        int removeCount,
        IReadOnlyList<MarkdownDocumentItem> replacements)
    {
        if (startIndex < 0 || startIndex > items.Count)
        {
            throw new InvalidOperationException("target_index_out_of_range");
        }

        if (removeCount > 0 && startIndex < items.Count)
        {
            items.RemoveRange(startIndex, Math.Min(removeCount, items.Count - startIndex));
        }

        if (replacements.Count > 0)
        {
            items.InsertRange(startIndex, replacements);
        }
    }
}
