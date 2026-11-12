using ChatClient.Api.Services.BuiltIn;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class UserProfileStoreTests
{
    [Fact]
    public async Task NewProfile_IsEmptyWithoutDefaults_AndAbsentKeyIsNotAnError()
    {
        using var fixture = new ProfileFixture();
        var profile = fixture.CreateStore();

        Assert.Empty(await profile.GetAllAsync());
        var (exists, value) = await profile.GetAsync("unknown");
        Assert.False(exists);
        Assert.Null(value);
        Assert.False(File.Exists(profile.FilePath));
    }

    [Fact]
    public async Task SetGetAndDelete_NormalizeValuesAndKeys_AndPersistAcrossInstances()
    {
        using var fixture = new ProfileFixture();
        var first = fixture.CreateStore();

        await first.SetAsync(" DisplayName ", "  Alice  ");
        await first.SetAsync("displayname", " Dmitry ");
        await first.SetAsync(" City ", " Malaga ");
        var second = fixture.CreateStore();

        Assert.Equal("Dmitry", (await second.GetAsync("DISPLAYNAME")).Value);
        Assert.Equal("Malaga", (await second.GetAsync("city")).Value);
        Assert.Equal(2, (await second.GetAllAsync()).Count);
        Assert.Equal("Dmitry", (await second.GetAllAsync())["DisplayName"]);

        var deleted = await second.SetAsync("displayname", "  ");
        Assert.True(deleted.Deleted);
        Assert.Null(deleted.Value);
        Assert.True((await second.SetAsync("displayname", null)).Deleted);
        Assert.False((await first.GetAsync("displayName")).Exists);
        Assert.Equal("Malaga", (await first.GetAsync("city")).Value);
    }

    [Fact]
    public async Task Save_ReplacesAllRowsAndKeepsAStableFlatJson()
    {
        using var fixture = new ProfileFixture();
        var profile = fixture.CreateStore();
        await profile.SetAsync("removed", "old");

        await profile.SaveAsync([
            new(" z ", " last "),
            new("A", " first "),
            new("empty", "  ")
        ]);

        Assert.Equal(2, (await profile.GetAllAsync()).Count);
        Assert.False((await profile.GetAllAsync()).ContainsKey("removed"));
        var json = await File.ReadAllTextAsync(profile.FilePath);
        Assert.True(json.IndexOf("\"A\"", StringComparison.Ordinal) <
                    json.IndexOf("\"z\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("A").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("values", out _));
    }

    [Fact]
    public async Task Save_RejectsDuplicatesWithoutChangingExistingProfile()
    {
        using var fixture = new ProfileFixture();
        var profile = fixture.CreateStore();
        await profile.SetAsync("name", "Alice");

        await Assert.ThrowsAsync<InvalidOperationException>(() => profile.SaveAsync([
            new("Name", "Bob"),
            new(" name ", "Charlie")
        ]));

        Assert.Equal("Alice", (await profile.GetAsync("name")).Value);
        await Assert.ThrowsAsync<ArgumentException>(() => profile.SetAsync(" ", "bad"));
        await Assert.ThrowsAsync<ArgumentException>(() => profile.GetAsync(""));
    }

    [Fact]
    public async Task LegacyDocument_MigratesOnlyValues_AndSavesBackup()
    {
        using var fixture = new ProfileFixture();
        var profile = fixture.CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(profile.FilePath)!);
        var legacy = """
            {
              "serverDescription": "old",
              "definitions": [{"key":"name","defaultValue":"Default"}],
              "values": {"name":" Dmitry ", "city":" Malaga ", "empty":" "},
              "memories": [{"id":"1","text":"not migrated"}]
            }
            """;
        await File.WriteAllTextAsync(profile.FilePath, legacy);

        var values = await profile.GetAllAsync();

        Assert.Equal(2, values.Count);
        Assert.Equal("Dmitry", values["name"]);
        Assert.Equal("Malaga", values["city"]);
        Assert.DoesNotContain("Default", values.Values);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(profile.FilePath)!, "user_profile.json.legacy-*.bak"));
        Assert.Equal(legacy, await File.ReadAllTextAsync(backup));
        var migrated = await File.ReadAllTextAsync(profile.FilePath);
        Assert.DoesNotContain("memories", migrated);
        Assert.DoesNotContain("definitions", migrated);
        Assert.Equal(2, (await fixture.CreateStore().GetAllAsync()).Count);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(profile.FilePath)!, "*.bak"));
    }

    [Fact]
    public async Task FlatLegacyMap_IsReadAsProfile()
    {
        using var fixture = new ProfileFixture();
        var profile = fixture.CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(profile.FilePath)!);
        await File.WriteAllTextAsync(profile.FilePath,
            """{"displayName":" Alice ","age":35,"active":true,"unused":null}""");

        var values = await profile.GetAllAsync();
        Assert.Equal("Alice", values["displayName"]);
        Assert.Equal("35", values["age"]);
        Assert.Equal("true", values["active"]);
        Assert.False(values.ContainsKey("unused"));
    }

    [Fact]
    public async Task ConcurrentWriters_DoNotLoseUpdatesAcrossStoreInstances()
    {
        using var fixture = new ProfileFixture();
        var stores = Enumerable.Range(0, 12).Select(_ => fixture.CreateStore()).ToArray();
        await Task.WhenAll(stores.Select((store, index) =>
            store.SetAsync($"key{index}", $"value{index}")));

        Assert.Equal(12, (await fixture.CreateStore().GetAllAsync()).Count);
    }

    private sealed class ProfileFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ollamachat-profile-tests", Guid.NewGuid().ToString("N"));

        public UserProfileStore CreateStore() => new(Path.Combine(_root, "UserData", "user_profile.json"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
