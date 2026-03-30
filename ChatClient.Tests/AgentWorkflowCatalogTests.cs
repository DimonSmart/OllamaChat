using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class AgentWorkflowCatalogTests
{
    [Fact]
    public async Task GetRequiredAsync_BuildsInterviewCoachWorkflowWithExpectedAgentsAndHandoffs()
    {
        var catalog = new AgentWorkflowCatalog(new StubMcpServerConfigService(
        [
            new McpServerConfig
            {
                Id = Guid.NewGuid(),
                Name = "Built-in Task Session MCP Server"
            },
            new McpServerConfig
            {
                Id = Guid.NewGuid(),
                Name = "Built-in Document Intake MCP Server"
            }
        ]));

        var template = await catalog.GetRequiredAsync("interview-coach-fixed-handoff");

        Assert.Equal("interview-coach-fixed-handoff", template.Id);
        Assert.True(template.Assessment.FluentBuilderIsSufficient);
        Assert.False(template.Assessment.ExistingSavedAgentsAreReusable);

        var workflow = template.Workflow;
        Assert.Equal("triage", workflow.StartAgentId);
        Assert.Equal(["triage", "receptionist", "behavioural", "technical", "summarizer"], workflow.Agents.Select(static agent => agent.Id).ToArray());
        Assert.Equal(["resume", "job_description"], workflow.StartInputs.Select(static input => input.Key).ToArray());

        var triage = Assert.Single(workflow.Agents, static agent => agent.Id == "triage");
        Assert.Equal("triage", triage.AgentDraft.ShortName);
        Assert.Contains("routing", triage.AgentDraft.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(triage.CapabilityRequirements, static capability => capability.Key == "task-session-store");

        var receptionist = Assert.Single(workflow.Agents, static agent => agent.Id == "receptionist");
        var taskBinding = Assert.Single(
            receptionist.AgentDraft.McpServerBindings,
            static binding => string.Equals(binding.ServerName, "Built-in Task Session MCP Server", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(taskBinding.SelectedTools, static tool => string.Equals(tool, "session_create", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(taskBinding.SelectedTools, static tool => string.Equals(tool, "session_attach_document", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("start form", receptionist.AgentDraft.Content, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "triage" &&
            handoff.ToAgentId == "receptionist" &&
            !handoff.IsFallback);
        Assert.Contains(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "receptionist" &&
            handoff.ToAgentId == "triage" &&
            handoff.IsFallback);
        Assert.Contains(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "technical" &&
            handoff.ToAgentId == "summarizer" &&
            !handoff.IsFallback);
    }

    [Fact]
    public async Task GetRequiredAsync_MarksDocumentIntakeAsPartialWhenOnlyMarkdownServerExists()
    {
        var catalog = new AgentWorkflowCatalog(new StubMcpServerConfigService(
        [
            new McpServerConfig
            {
                Id = Guid.NewGuid(),
                Name = "Built-in Markdown Document MCP Server"
            }
        ]));

        var template = await catalog.GetRequiredAsync("interview-coach-fixed-handoff");
        Assert.Contains(
            template.Assessment.MissingProjectPieces,
            static note => note.Contains("not full resume parsing like MarkItDown", StringComparison.OrdinalIgnoreCase));

        var receptionist = Assert.Single(template.Workflow.Agents, static agent => agent.Id == "receptionist");
        var sessionStore = Assert.Single(receptionist.CapabilityRequirements);
        Assert.Equal("task-session-store", sessionStore.Key);
        Assert.Equal(AgentWorkflowCapabilityAvailability.Missing, sessionStore.Availability);
    }

    [Fact]
    public async Task GetRequiredAsync_MarksCriticalCapabilitiesAvailableWhenMatchingServersExist()
    {
        var catalog = new AgentWorkflowCatalog(new StubMcpServerConfigService(
        [
            new McpServerConfig
            {
                Id = Guid.NewGuid(),
                Name = "Built-in Document Intake MCP Server"
            },
            new McpServerConfig
            {
                Id = Guid.NewGuid(),
                Name = "Built-in Task Session MCP Server"
            }
        ]));

        var template = await catalog.GetRequiredAsync("interview-coach-fixed-handoff");
        var receptionist = Assert.Single(template.Workflow.Agents, static agent => agent.Id == "receptionist");

        Assert.All(receptionist.CapabilityRequirements, static requirement =>
            Assert.Equal(AgentWorkflowCapabilityAvailability.Available, requirement.Availability));
        Assert.Contains(
            template.Assessment.MissingProjectPieces,
            static note => note.Contains("official handoff runtime", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubMcpServerConfigService(IReadOnlyCollection<IMcpServerDescriptor> servers) : IMcpServerConfigService
    {
        public Task<IReadOnlyCollection<IMcpServerDescriptor>> GetAllAsync() => Task.FromResult(servers);

        public Task<IMcpServerDescriptor?> GetByIdAsync(Guid serverId) =>
            Task.FromResult(servers.FirstOrDefault(server => server.Id == serverId));

        public Task CreateAsync(McpServerConfig serverConfig) => throw new NotSupportedException();

        public Task UpdateAsync(McpServerConfig serverConfig) => throw new NotSupportedException();

        public Task DeleteAsync(Guid serverId) => throw new NotSupportedException();

        public Task<McpServerConfig> InstallFromLinkAsync(string link) => throw new NotSupportedException();
    }
}
