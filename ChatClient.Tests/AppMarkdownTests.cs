using ChatClient.Api.Client.Markdown;

namespace ChatClient.Tests;

public class AppMarkdownTests
{
    [Fact]
    public void ToHtmlModelOutput_UnwrapsOuterMarkdownFence()
    {
        const string markdown = """
```markdown
Not entirely; they will replace **some of our functions**.

Does that concern you?
```
""";

        string html = AppMarkdown.ToHtmlModelOutput(markdown);

        Assert.Contains("<strong>some of our functions</strong>", html);
        Assert.DoesNotContain("<pre><code", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_PreservesRegularCodeFence()
    {
        const string markdown = """
```csharp
Console.WriteLine("Hello");
```
""";

        string html = AppMarkdown.ToHtml(markdown);

        Assert.Contains("<pre><code class=\"language-csharp\">", html);
    }
}
