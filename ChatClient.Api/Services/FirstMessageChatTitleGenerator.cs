using ChatClient.Application.Services;
using System.Globalization;
using System.Text;

namespace ChatClient.Api.Services;

public sealed class FirstMessageChatTitleGenerator : IChatTitleGenerator
{
    public string Generate(string firstUserMessage)
    {
        var normalized = string.Join(" ", (firstUserMessage ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized))
            return "New chat";
        var elements = StringInfo.GetTextElementEnumerator(normalized);
        var builder = new StringBuilder();
        var count = 0;
        while (elements.MoveNext() && count++ < 32)
            builder.Append(elements.GetTextElement());
        return builder.ToString();
    }
}
