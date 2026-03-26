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

    [Fact]
    public void ToHtmlModelOutput_NormalizesBraveImageProxyUrls()
    {
        const string markdown = """
![FastBot](https://imgs.search.brave.com/VKvr4h7jRu5ZM7jOCzQico3NHITMKQFyTZromue9Kio/rs:fit:200:200:1:0/g:ce/aHR0cHM6Ly93d3cudGhlY29uc3RydWN0LmFpL3dwLWNvbnRlbnQvdXBsb2Fkcy8yMDI1LzA0L0Zhc3Rib3QtQ29tcG9uZW50LmpwZw)
""";

        string html = AppMarkdown.ToHtmlModelOutput(markdown);

        Assert.Contains("https://www.theconstruct.ai/wp-content/uploads/2025/04/Fastbot-Component.jpg", html);
        Assert.DoesNotContain("https://imgs.search.brave.com/", html, StringComparison.OrdinalIgnoreCase);
    }
}
