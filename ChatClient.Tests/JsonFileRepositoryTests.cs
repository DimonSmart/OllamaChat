using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public class JsonFileRepositoryTests
{
    private sealed class Sample
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    [Fact]
    public async Task WriteAndReadAsync_PersistsData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            var repo = new JsonFileRepository<Sample>(path, NullLogger.Instance);
            var sample = new Sample { Name = "test", Value = 1 };
            await repo.WriteAsync(sample);
            var loaded = await repo.ReadAsync();
            Assert.NotNull(loaded);
            Assert.Equal("test", loaded!.Name);
            Assert.Equal(1, loaded.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateAsync_CreatesAndModifiesData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            var repo = new JsonFileRepository<Sample>(path, NullLogger.Instance);
            await repo.UpdateAsync(d =>
            {
                d.Name = "updated";
                d.Value = 2;
                return Task.CompletedTask;
            }, new Sample());
            var loaded = await repo.ReadAsync();
            Assert.NotNull(loaded);
            Assert.Equal("updated", loaded!.Name);
            Assert.Equal(2, loaded.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateAsync_SerializesAccessAcrossRepositoryInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            var repo1 = new JsonFileRepository<Sample>(path, NullLogger.Instance);
            var repo2 = new JsonFileRepository<Sample>(path, NullLogger.Instance);
            await repo1.WriteAsync(new Sample { Name = "counter", Value = 0 });

            var tasks = Enumerable.Range(0, 24)
                .Select(index =>
                {
                    var repository = index % 2 == 0 ? repo1 : repo2;
                    return repository.UpdateAsync(
                        async sample =>
                        {
                            var currentValue = sample.Value;
                            await Task.Delay(5);
                            sample.Value = currentValue + 1;
                        },
                        new Sample());
                });

            await Task.WhenAll(tasks);

            var loaded = await repo1.ReadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(24, loaded!.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

