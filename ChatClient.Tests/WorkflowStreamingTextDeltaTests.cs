using ChatClient.Api.Client.Services.Agentic;

namespace ChatClient.Tests;

public class WorkflowStreamingTextDeltaTests
{
    [Fact]
    public void GetAppendText_ReturnsIncoming_WhenCurrentIsEmpty()
    {
        var appendText = WorkflowStreamingTextDelta.GetAppendText(
            string.Empty,
            "Да, начнем.");

        Assert.Equal("Да, начнем.", appendText);
    }

    [Fact]
    public void GetAppendText_ReturnsOnlySuffix_ForGrowingSnapshot()
    {
        var appendText = WorkflowStreamingTextDelta.GetAppendText(
            "Да, начнем.",
            "Да, начнем.\n\nПервый вопрос:");

        Assert.Equal("\n\nПервый вопрос:", appendText);
    }

    [Fact]
    public void GetAppendText_ReturnsEmpty_ForDuplicateSnapshot()
    {
        var appendText = WorkflowStreamingTextDelta.GetAppendText(
            "Первый вопрос: расскажите о ситуации",
            "Первый вопрос: расскажите о ситуации");

        Assert.Equal(string.Empty, appendText);
    }

    [Fact]
    public void GetAppendText_RemovesSuffixOverlap()
    {
        var appendText = WorkflowStreamingTextDelta.GetAppendText(
            "Первый вопрос: расскажите о",
            "о ситуации, когда вам пришлось взять инициативу.");

        Assert.Equal(" ситуации, когда вам пришлось взять инициативу.", appendText);
    }

    [Fact]
    public void GetAppendText_ReturnsEmpty_WhenIncomingAlreadyContained()
    {
        var appendText = WorkflowStreamingTextDelta.GetAppendText(
            "Да, начнем.\n\nПервый вопрос:",
            "Первый вопрос:");

        Assert.Equal(string.Empty, appendText);
    }
}
