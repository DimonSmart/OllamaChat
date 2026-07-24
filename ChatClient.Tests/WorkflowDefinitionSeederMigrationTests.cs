using ChatClient.Api.Services.Seed;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowDefinitionSeederMigrationTests
{
    [Fact]
    public void TryMigrateKnownBuiltInWorkflow_ReplacesOnlyLegacyPhilosopherReferences()
    {
        var createdAt = DateTime.UtcNow.AddDays(-2);
        var updatedAt = DateTime.UtcNow.AddDays(-1);
        var workflow = new SavedWorkflowDefinition
        {
            Id = Guid.NewGuid(),
            WorkflowId = "philosopher-battle-group-chat",
            Kind = "group-chat",
            DisplayName = "Customized Philosopher Battle",
            Description = "User-customized built-in workflow.",
            SourceCode =
                """
                var workflow = WorkflowDefinitionBuilder
                    .New("philosopher-battle-group-chat", "Customized Philosopher Battle")
                    .Agent("debater_a", agent => agent
                        .FromSavedAgent("Immanuel Kant"))
                    .Agent("debater_b", agent => agent
                        .FromSavedAgent("Friedrich Nietzsche"))
                    .UseGroupChat(groupChat => groupChat
                        .Participants("debater_a", "debater_b")
                        .UseCustomManager("custom-manager", maximumIterations: 17))
                    .Build();

                workflow
                """,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
        var originalId = workflow.Id;

        var migrated = WorkflowDefinitionSeeder.TryMigrateKnownBuiltInWorkflow(workflow);

        Assert.True(migrated);
        Assert.Equal(originalId, workflow.Id);
        Assert.Equal(createdAt, workflow.CreatedAt);
        Assert.True(workflow.UpdatedAt > updatedAt);
        Assert.Contains(
            ".UseAgentById(\"ab38adc6-74a2-4ccc-924b-eb1bce9d0985\")",
            workflow.SourceCode,
            StringComparison.Ordinal);
        Assert.Contains(
            ".UseAgentById(\"8bb2a12d-d5fd-440b-b622-b46d8897556a\")",
            workflow.SourceCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".FromSavedAgent(", workflow.SourceCode, StringComparison.Ordinal);
        Assert.Contains(
            ".UseCustomManager(\"custom-manager\", maximumIterations: 17)",
            workflow.SourceCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryMigrateKnownBuiltInWorkflow_DoesNotRewriteExplicitNameBasedDsl()
    {
        var workflow = new SavedWorkflowDefinition
        {
            Id = Guid.NewGuid(),
            WorkflowId = "philosopher-battle-group-chat",
            Kind = "group-chat",
            DisplayName = "Name-based Demo",
            SourceCode =
                """
                .UseAgentByName("Immanuel Kant")
                .UseAgentByName("Friedrich Nietzsche")
                """,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var migrated = WorkflowDefinitionSeeder.TryMigrateKnownBuiltInWorkflow(workflow);

        Assert.False(migrated);
        Assert.Contains(".UseAgentByName(\"Immanuel Kant\")", workflow.SourceCode, StringComparison.Ordinal);
        Assert.Contains(".UseAgentByName(\"Friedrich Nietzsche\")", workflow.SourceCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryMigrateKnownBuiltInWorkflow_UpdatesLegacyAutomaticTurnLimit()
    {
        var workflow = new SavedWorkflowDefinition
        {
            Id = Guid.NewGuid(),
            WorkflowId = "philosopher-battle-group-chat",
            Kind = "group-chat",
            DisplayName = "Philosopher Battle Group Chat",
            Description = "Built-in workflow.",
            SourceCode =
                """
                var workflow = WorkflowDefinitionBuilder
                    .New("philosopher-battle-group-chat", "Philosopher Battle Group Chat")
                    .RunAutonomously(maxAutomaticTurns: 10, completionPhase: "complete", completionSummaryLabel: "final")
                    .Build();
                """,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var migrated = WorkflowDefinitionSeeder.TryMigrateKnownBuiltInWorkflow(workflow);

        Assert.True(migrated);
        Assert.Contains(".RunAutonomously(maxAutomaticTurns: 42", workflow.SourceCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".RunAutonomously(maxAutomaticTurns: 10", workflow.SourceCode, StringComparison.Ordinal);
    }
}
