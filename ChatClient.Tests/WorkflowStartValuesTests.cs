using ChatClient.Api.AgentWorkflows;

namespace ChatClient.Tests;

public sealed class WorkflowStartValuesTests
{
    [Fact]
    public void RequireInt32_ParsesInvariantIntegerWithinRange()
    {
        var values = new WorkflowStartValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rounds"] = "5"
        });

        Assert.Equal(5, values.RequireInt32("rounds", min: 1, max: 20));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("5.5")]
    public void RequireInt32_RejectsNonIntegerValues(string rawValue)
    {
        var values = new WorkflowStartValues(new Dictionary<string, string>
        {
            ["rounds"] = rawValue
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            values.RequireInt32("rounds", min: 1, max: 20));

        Assert.Contains("expects a whole number", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    public void RequireInt32_RejectsValuesOutsideRange(string rawValue)
    {
        var values = new WorkflowStartValues(new Dictionary<string, string>
        {
            ["rounds"] = rawValue
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            values.RequireInt32("rounds", min: 1, max: 20));

        Assert.Contains("between 1 and 20", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_ResolvesMaximumIterationsFromStartValues()
    {
        var program = GroupChatManagerPrograms
            .PrefixCycleSuffix(["host"], ["a", "b"], ["judge"])
            .WithMaximumIterationsResolver(start =>
                checked(start.RequireInt32("rounds", min: 1, max: 20) * 2 + 2));
        var values = new WorkflowStartValues(new Dictionary<string, string>
        {
            ["rounds"] = "5"
        });

        Assert.Equal(12, program.ResolveMaximumIterations(values, fallback: 40));
    }
}
