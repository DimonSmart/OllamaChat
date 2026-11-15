using System.Reflection;
using ChatClient.Api.Client.Pages;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Moq;

namespace ChatClient.Tests;

public sealed class AgentSkillsProfilesPageTests
{
    private const BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task AddProfile_SavesNewProfileInsteadOfUpdatingNonexistentProfile()
    {
        var service = CreateService();
        AgentSkillsProfile? created = null;
        service.Setup(x => x.CreateAsync(It.IsAny<AgentSkillsProfile>()))
            .Callback<AgentSkillsProfile>(profile => created = profile)
            .Returns(Task.CompletedTask);
        var page = CreatePage(service.Object);

        Invoke(page, "Create");
        var model = EditingModel(page);
        model.Name = "IDD skills";
        var pluginId = Guid.NewGuid();
        model.PluginIds.Add(pluginId);

        await (Task)Invoke(page, "Save")!;

        service.Verify(x => x.CreateAsync(It.IsAny<AgentSkillsProfile>()), Times.Once);
        service.Verify(x => x.UpdateAsync(It.IsAny<AgentSkillsProfile>()), Times.Never);
        Assert.NotNull(created);
        Assert.Equal(pluginId, Assert.Single(created.PluginIds));
    }

    [Fact]
    public async Task EditProfile_UpdatesExistingProfile()
    {
        var service = CreateService();
        AgentSkillsProfile? updated = null;
        service.Setup(x => x.UpdateAsync(It.IsAny<AgentSkillsProfile>()))
            .Callback<AgentSkillsProfile>(profile => updated = profile)
            .Returns(Task.CompletedTask);
        var page = CreatePage(service.Object);
        var originalId = Guid.NewGuid();

        Invoke(page, "Edit", new AgentSkillsProfile { Id = originalId, Name = "Before" });
        EditingModel(page).Name = "After";

        await (Task)Invoke(page, "Save")!;

        service.Verify(x => x.CreateAsync(It.IsAny<AgentSkillsProfile>()), Times.Never);
        service.Verify(x => x.UpdateAsync(It.IsAny<AgentSkillsProfile>()), Times.Once);
        Assert.NotNull(updated);
        Assert.Equal(originalId, updated.Id);
        Assert.Equal("After", updated.Name);
    }

    private static Mock<IAgentSkillsProfileService> CreateService()
    {
        var service = new Mock<IAgentSkillsProfileService>();
        service.Setup(x => x.GetAllAsync())
            .ReturnsAsync((IReadOnlyCollection<AgentSkillsProfile>)Array.Empty<AgentSkillsProfile>());
        return service;
    }

    private static AgentSkillsProfiles CreatePage(IAgentSkillsProfileService service)
    {
        var page = new AgentSkillsProfiles();
        typeof(AgentSkillsProfiles).GetProperty("Service", InstanceMembers)!
            .SetValue(page, service);
        return page;
    }

    private static AgentSkillsProfile EditingModel(AgentSkillsProfiles page) =>
        (AgentSkillsProfile)typeof(AgentSkillsProfiles)
            .GetField("model", InstanceMembers)!.GetValue(page)!;

    private static object? Invoke(AgentSkillsProfiles page, string method, params object[] args) =>
        typeof(AgentSkillsProfiles)
            .GetMethod(method, InstanceMembers)!.Invoke(page, args);
}
