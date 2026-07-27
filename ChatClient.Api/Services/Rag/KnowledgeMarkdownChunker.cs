using ChatClient.Domain.Models;
using Microsoft.ML.Tokenizers;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatClient.Api.Services.Rag;

public sealed partial class KnowledgeMarkdownChunker : IKnowledgeMarkdownChunker
{
    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base", null, null);

    public IReadOnlyList<KnowledgeChunkRecord> Chunk(string fileName, string markdown, int maxTokens, int overlapTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        if (maxTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        if (overlapTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(overlapTokens));

        var result = new List<KnowledgeChunkRecord>();
        var current = new StringBuilder();
        string? currentSection = null;
        foreach (var block in ReadBlocks(markdown))
        {
            if (current.Length > 0 && !string.Equals(currentSection, block.Section, StringComparison.Ordinal))
            {
                AddChunk(result, fileName, current.ToString(), currentSection);
                current.Clear();
            }
            foreach (var part in SplitOversizedBlock(block.Content, maxTokens))
            {
                var candidate = current.Length == 0 ? part : $"{current}\n\n{part}";
                if (CountTokens(candidate) <= maxTokens)
                {
                    if (current.Length == 0)
                        currentSection = block.Section;
                    else if (currentSection is null)
                        currentSection = block.Section;
                    current.Clear().Append(candidate);
                    continue;
                }

                AddChunk(result, fileName, current.ToString(), currentSection);
                var overlap = CreateOverlap(result[^1].Content, overlapTokens);
                current.Clear();
                if (!string.IsNullOrWhiteSpace(overlap) && CountTokens($"{overlap}\n\n{part}") <= maxTokens)
                    current.Append(overlap);
                currentSection = block.Section;
                if (current.Length > 0)
                    current.Append("\n\n");
                current.Append(part);
            }
        }
        AddChunk(result, fileName, current.ToString(), currentSection);
        return result;
    }

    private IEnumerable<MarkdownBlock> ReadBlocks(string markdown)
    {
        var headings = new string?[6];
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length;)
        {
            var heading = HeadingPattern().Match(lines[index]);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                headings[level - 1] = heading.Groups[2].Value.Trim();
                Array.Clear(headings, level, headings.Length - level);
                index++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            var section = string.Join(" > ", headings.Where(static value => !string.IsNullOrWhiteSpace(value))!);
            var block = new StringBuilder();
            if (FencePattern().IsMatch(lines[index]))
            {
                var fence = lines[index].TrimStart()[..3];
                do
                {
                    block.AppendLine(lines[index]);
                    index++;
                } while (index < lines.Length && !lines[index].TrimStart().StartsWith(fence, StringComparison.Ordinal));
                if (index < lines.Length)
                    block.AppendLine(lines[index++]);
            }
            else
            {
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !HeadingPattern().IsMatch(lines[index]))
                    block.AppendLine(lines[index++]);
            }
            var content = block.ToString().Trim();
            if (content.Length > 0)
                yield return new MarkdownBlock(content, string.IsNullOrWhiteSpace(section) ? null : section);
        }
    }

    private IEnumerable<string> SplitOversizedBlock(string content, int maxTokens)
    {
        if (CountTokens(content) <= maxTokens)
        {
            yield return content;
            yield break;
        }

        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var chunk = new StringBuilder();
        foreach (var word in words)
        {
            var candidate = chunk.Length == 0 ? word : $"{chunk} {word}";
            if (chunk.Length > 0 && CountTokens(candidate) > maxTokens)
            {
                yield return chunk.ToString();
                chunk.Clear();
                foreach (var fragment in SplitTokenLimited(word, maxTokens))
                {
                    if (CountTokens(fragment) == maxTokens)
                        yield return fragment;
                    else
                        chunk.Append(fragment);
                }
            }
            else
            {
                chunk.Clear().Append(candidate);
            }
        }
        if (chunk.Length > 0)
            yield return chunk.ToString();
    }

    private IEnumerable<string> SplitTokenLimited(string value, int maxTokens)
    {
        while (value.Length > 0)
        {
            if (CountTokens(value) <= maxTokens)
            {
                yield return value;
                yield break;
            }

            var length = value.Length;
            while (length > 1 && CountTokens(value[..length]) > maxTokens)
                length /= 2;
            while (length < value.Length && CountTokens(value[..(length + 1)]) <= maxTokens)
                length++;
            yield return value[..length];
            value = value[length..];
        }
    }

    private string CreateOverlap(string content, int overlapTokens)
    {
        if (overlapTokens == 0)
            return string.Empty;

        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var overlap = new StringBuilder();
        for (var index = words.Length - 1; index >= 0; index--)
        {
            var candidate = overlap.Length == 0 ? words[index] : $"{words[index]} {overlap}";
            if (CountTokens(candidate) > overlapTokens)
                break;
            overlap.Clear().Append(candidate);
        }
        return overlap.ToString();
    }

    private int CountTokens(string value) => _tokenizer.CountTokens(value);

    private static void AddChunk(List<KnowledgeChunkRecord> chunks, string fileName, string content, string? section)
    {
        if (!string.IsNullOrWhiteSpace(content))
            chunks.Add(new KnowledgeChunkRecord { FileName = fileName, ChunkIndex = chunks.Count, Content = content, Section = section });
    }

    private sealed record MarkdownBlock(string Content, string? Section);

    [GeneratedRegex("^(#{1,6})\\s+(.+?)\\s*#*\\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex("^\\s*(```|~~~)")]
    private static partial Regex FencePattern();
}
