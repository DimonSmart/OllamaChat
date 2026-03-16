using ChatClient.Api.Client.Components.Planning;

namespace ChatClient.Tests;

public class PlanningPreviewScenariosTests
{
    [Fact]
    public void PreviewDescriptions_AreNormalizedBeforeUiReadsThem()
    {
        var happyPath = PlanningPreviewScenarios.Get("happy-path");
        var replanFailure = PlanningPreviewScenarios.Get("replan-failure");

        Assert.NotNull(happyPath);
        Assert.NotNull(replanFailure);

        Assert.StartsWith("\u0413\u043e\u0442\u043e\u0432\u044b\u0439", happyPath!.Description, StringComparison.Ordinal);
        Assert.StartsWith("\u0421\u0446\u0435\u043d\u0430\u0440\u0438\u0439", replanFailure!.Description, StringComparison.Ordinal);
    }
}
