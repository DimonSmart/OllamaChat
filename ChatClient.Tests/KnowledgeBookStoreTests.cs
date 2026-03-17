using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class KnowledgeBookStoreTests
{
    [Fact]
    public async Task InsertSection_CreatesNestedStructure_AndExportsMarkdown()
    {
        await using var fixture = new KnowledgeBookFixture();
        var store = fixture.CreateStore();

        var cooking = await store.InsertSectionAsync(
            title: "Cooking",
            anchorOutline: null,
            asChild: false,
            contentMarkdown: null,
            cancellationToken: CancellationToken.None);

        var pastry = await store.InsertSectionAsync(
            title: "Pastry",
            anchorOutline: cooking.Outline,
            asChild: true,
            contentMarkdown: null,
            cancellationToken: CancellationToken.None);

        await store.InsertSectionAsync(
            title: "Eclairs",
            anchorOutline: pastry.Outline,
            asChild: true,
            contentMarkdown: "Use choux pastry and pastry cream.",
            cancellationToken: CancellationToken.None);

        var rootHeadings = await store.ListHeadingsAsync("0", 1, CancellationToken.None);
        var rootHeading = Assert.Single(rootHeadings);
        Assert.Equal("1", rootHeading.Outline);
        Assert.Equal("Cooking", rootHeading.Title);

        var nestedSection = await store.GetSectionAsync("1.1.1", CancellationToken.None);
        Assert.Equal("1.1.1", nestedSection.Outline);
        Assert.Equal("Eclairs", nestedSection.Title);
        Assert.Contains("choux pastry", nestedSection.ContentMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Cooking", "Pastry", "Eclairs"], nestedSection.Path);

        var markdown = await store.ExportMarkdownAsync(CancellationToken.None);
        Assert.Contains("# Cooking", markdown, StringComparison.Ordinal);
        Assert.Contains("## Pastry", markdown, StringComparison.Ordinal);
        Assert.Contains("### Eclairs", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateSection_ReplacesMarkdown()
    {
        await using var fixture = new KnowledgeBookFixture();
        var store = fixture.CreateStore();

        var section = await store.InsertSectionAsync(
            title: "Readme",
            anchorOutline: null,
            asChild: false,
            contentMarkdown: "Old text",
            cancellationToken: CancellationToken.None);

        var updated = await store.UpdateSectionAsync(
            section.Outline,
            "New text",
            CancellationToken.None);

        Assert.Equal(section.Outline, updated.Outline);
        Assert.Equal("New text", updated.ContentMarkdown);
    }

    [Fact]
    public async Task InsertSection_UsesVirtualStartAnchor_ForBeginningOfLevel()
    {
        await using var fixture = new KnowledgeBookFixture();
        var store = fixture.CreateStore();

        await store.InsertSectionAsync("Beta", null, false, null, CancellationToken.None);
        await store.InsertSectionAsync("Gamma", null, false, null, CancellationToken.None);
        var alpha = await store.InsertSectionAsync("Alpha", "0", false, null, CancellationToken.None);

        var rootHeadings = await store.ListHeadingsAsync("0", 1, CancellationToken.None);
        Assert.Equal("1", alpha.Outline);
        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            rootHeadings.Select(static heading => heading.Title).ToArray());
    }

    [Fact]
    public async Task GetSection_RootOutline_UsesZero()
    {
        await using var fixture = new KnowledgeBookFixture();
        var store = fixture.CreateStore();

        var root = await store.GetSectionAsync("0", CancellationToken.None);

        Assert.Equal("0", root.Outline);
        Assert.Equal("Knowledge Book", root.Title);
    }

    [Fact]
    public void GetContext_UsesKnowledgeFileOnly_AndIgnoresRoots()
    {
        var tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var outsideFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "book.json");
            var binding = new McpServerSessionBinding
            {
                ServerName = "Built-in Knowledge Book MCP Server",
                Roots = [tempRoot.FullName],
                Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [KnowledgeBookStore.KnowledgeFileParameter] = outsideFile
                }
            };

            var store = new KnowledgeBookStore(new McpServerSessionContext(binding));

            var context = store.GetContext();
            Assert.Equal(Path.GetFullPath(outsideFile), context.KnowledgeFile);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    private sealed class KnowledgeBookFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        public KnowledgeBookStore CreateStore()
        {
            var binding = new McpServerSessionBinding
            {
                ServerName = "Built-in Knowledge Book MCP Server",
                Roots = [_root.FullName],
                Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [KnowledgeBookStore.KnowledgeFileParameter] = Path.Combine(_root.FullName, "book.json")
                }
            };

            return new KnowledgeBookStore(new McpServerSessionContext(binding));
        }

        public ValueTask DisposeAsync()
        {
            if (_root.Exists)
            {
                _root.Delete(recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
