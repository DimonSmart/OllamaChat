using System.Text.RegularExpressions;

namespace ChatClient.Api.Client.Utils;

public static class ThoughtParser
{
    private static readonly Regex _thinkRegex = new Regex(
        @"<think>([\s\S]*?)</think>([\s\S]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses the string into two parts:
    ///  - Think: content between <think> and </think> tags
    ///  - Answer: everything that remains after </think>
    /// If no tags are present - Think will be empty, and Answer = original string.
    /// </summary>
    public static (string Think, string Answer) SplitThinkAndAnswer(string source)
    {
        if (string.IsNullOrEmpty(source))
            return (string.Empty, string.Empty);

        var match = _thinkRegex.Match(source);
        if (match.Success)
        {
            // Group and trim whitespace at the edges
            var think = match.Groups[1].Value.Trim();
            var answer = match.Groups[2].Value.Trim();
            return (think, answer);
        }
        else
        {
            // No tags - everything is the answer
            return (string.Empty, source.Trim());
        }
    }
}