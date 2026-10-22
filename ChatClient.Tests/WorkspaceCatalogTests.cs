using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class WorkspaceCatalogTests
{
    private static (WorkspaceService Service, WorkspaceResolver Resolver) Create(string root)
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:RootPath"] = root }).Build();
        var directories = new WorkspaceDirectoryService();
        var service = new WorkspaceService(
            new WorkspaceRepository(settings, NullLogger<WorkspaceRepository>.Instance),
            directories, TimeProvider.System);
        return (service, new WorkspaceResolver(service, directories));
    }

    private static string Root()
    {
        var path = Path.Combine(Path.GetTempPath(), "ollamachat-workspaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Create_Rename_Resolve_AndDelete_KeepPhysicalFiles()
    {
        var root = Root();
        try
        {
            var folder = Path.Combine(root, "project");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "README.md"), "existing");
            var (service, resolver) = Create(root);
            var created = await service.CreateAsync(new WorkspaceDefinition
            {
                Id = Guid.NewGuid(), Name = " Project ", Path = folder + Path.DirectorySeparatorChar
            });
            Assert.NotEqual(Guid.Empty, created.Id);
            Assert.Equal("Project", created.Name);
            Assert.Equal(1, created.Revision);
            Assert.Equal(folder, created.Path);
            Assert.Equal(created.Id, (await resolver.ResolveAsync(new WorkspaceSelection(WorkspaceId: created.Id)))!.SourceWorkspaceId);

            var modified = created.Clone();
            modified.Name = "New name";
            await service.UpdateAsync(modified);
            Assert.Equal(2, (await service.GetByIdAsync(created.Id))!.Revision);
            await service.DeleteAsync(created.Id, revision: 2);
            Assert.Empty(await service.GetAllAsync());
            Assert.True(File.Exists(Path.Combine(folder, "README.md")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Detects_DuplicateName_Path_AndStaleRevision()
    {
        var root = Root();
        try
        {
            var one = Path.Combine(root, "one");
            var two = Path.Combine(root, "two");
            Directory.CreateDirectory(one);
            Directory.CreateDirectory(two);
            var (service, _) = Create(root);
            var first = await service.CreateAsync(new WorkspaceDefinition { Name = "One", Path = one });
            await Assert.ThrowsAsync<WorkspaceException>(() =>
                service.CreateAsync(new WorkspaceDefinition { Name = " one ", Path = two }));
            await Assert.ThrowsAsync<WorkspaceException>(() =>
                service.CreateAsync(new WorkspaceDefinition { Name = "Other", Path = one }));
            var second = await service.CreateAsync(new WorkspaceDefinition { Name = "Two", Path = two });
            var update = first.Clone();
            update.Name = "Renamed";
            await service.UpdateAsync(update);
            await Assert.ThrowsAsync<WorkspaceException>(() => service.UpdateAsync(first));
            await Assert.ThrowsAsync<WorkspaceException>(() => service.DeleteAsync(first.Id, first.Revision));
            Assert.Equal(2, (await service.GetAllAsync()).Count);
            Assert.NotNull(await service.GetByIdAsync(second.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentWriters_DoNotLoseRecords()
    {
        var root = Root();
        try
        {
            var (left, _) = Create(root);
            var (right, _) = Create(root);
            var paths = Enumerable.Range(0, 20).Select(i =>
            {
                var path = Path.Combine(root, "project" + i);
                Directory.CreateDirectory(path);
                return path;
            }).ToArray();
            await Task.WhenAll(paths.Select((p, i) => (i % 2 == 0 ? left : right)
                .CreateAsync(new WorkspaceDefinition { Name = "Workspace " + i, Path = p })));
            var all = await left.GetAllAsync();
            Assert.Equal(paths.Length, all.Count);
            all[0].Name = "Modified outside repository";
            Assert.DoesNotContain((await right.GetAllAsync()), x => x.Name == "Modified outside repository");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentUpdatesOfSameRevision_RejectStaleWriter()
    {
        var root = Root();
        try
        {
            var folder = Path.Combine(root, "source");
            Directory.CreateDirectory(folder);
            var (firstService, _) = Create(root);
            var (secondService, _) = Create(root);
            var saved = await firstService.CreateAsync(new WorkspaceDefinition
            {
                Name = "Original", Path = folder
            });
            var first = saved.Clone();
            var second = saved.Clone();
            first.Name = "First";
            second.Name = "Second";

            static async Task<string> AttemptAsync(WorkspaceService service, WorkspaceDefinition update)
            {
                try
                {
                    await service.UpdateAsync(update);
                    return "success";
                }
                catch (WorkspaceException ex) { return ex.Code; }
            }

            var outcomes = await Task.WhenAll(
                AttemptAsync(firstService, first),
                AttemptAsync(secondService, second));
            Assert.Contains("success", outcomes);
            Assert.Contains("workspace_revision_conflict", outcomes);
            Assert.Equal(2, (await firstService.GetByIdAsync(saved.Id))!.Revision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"Wrong\",\"Path\":\"/tmp\",\"Revision\":1}]")]
    public async Task InvalidJsonBlocksChanges_WithoutOverwriting(string invalid)
    {
        var root = Root();
        try
        {
            var file = Path.Combine(root, "UserData", "workspaces.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, invalid);
            var (service, _) = Create(root);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.CreateAsync(new WorkspaceDefinition { Name = "New", Path = root }));
            Assert.Equal(invalid, await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task EmptyCatalogReadDoesNotCreateUserData()
    {
        var root = Root();
        try
        {
            var (service, _) = Create(root);
            Assert.Empty(await service.GetAllAsync());
            Assert.False(Directory.Exists(Path.Combine(root, "UserData")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ResolverRejectsInvalidSelections_AndNeverFallsBack()
    {
        var root = Root();
        try
        {
            var (_, resolver) = Create(root);
            Assert.Null(await resolver.ResolveAsync(new WorkspaceSelection()));
            await Assert.ThrowsAsync<WorkspaceException>(() =>
                resolver.ResolveAsync(new WorkspaceSelection(WorkspaceId: Guid.Empty)));
            await Assert.ThrowsAsync<WorkspaceException>(() =>
                resolver.ResolveAsync(new WorkspaceSelection(WorkspaceId: Guid.NewGuid())));
            await Assert.ThrowsAsync<WorkspaceException>(() =>
                resolver.ResolveAsync(new WorkspaceSelection(DirectPath: "relative")));
            await Assert.ThrowsAsync<WorkspaceException>(() =>
                resolver.ResolveAsync(new WorkspaceSelection(Guid.NewGuid(), root)));
            await Assert.ThrowsAsync<WorkspaceException>(() =>
                resolver.ResolveAsync(new WorkspaceSelection(DirectPath: Path.Combine(root, "missing"))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SavedPathIsReResolvedOnlyOnNextRun()
    {
        var root = Root();
        try
        {
            var a = Path.Combine(root, "a");
            var b = Path.Combine(root, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            var (service, resolver) = Create(root);
            var item = await service.CreateAsync(new WorkspaceDefinition { Name = "A", Path = a });
            var original = await resolver.ResolveAsync(new WorkspaceSelection(WorkspaceId: item.Id));
            item.Path = b;
            await service.UpdateAsync(item);
            var next = await resolver.ResolveAsync(new WorkspaceSelection(WorkspaceId: item.Id));
            Assert.Equal(a, original!.Path);
            Assert.Equal(b, next!.Path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
