using ChatClient.Api.Client.Services.Agentic;

namespace ChatClient.Tests;

public sealed class WorkflowStreamingTextDeltaTests
{
    [Fact]
    public void IsDuplicateOfCurrentMessage_ReturnsTrue_ForExactDuplicate()
    {
        var isDuplicate = WorkflowStreamingTextDelta.IsDuplicateOfCurrentMessage(
            "Host intro",
            "Host intro");

        Assert.True(isDuplicate);
    }

    [Fact]
    public void IsDuplicateOfCurrentMessage_ReturnsFalse_ForCumulativeTranscript()
    {
        var isDuplicate = WorkflowStreamingTextDelta.IsDuplicateOfCurrentMessage(
            "Host intro",
            "Host intro\n\nKant:\n\nEducation must remain universal.");

        Assert.False(isDuplicate);
    }
}
