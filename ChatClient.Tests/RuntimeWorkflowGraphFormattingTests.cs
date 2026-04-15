using ChatClient.Api.Client.Components.Planning;
using System.Globalization;

namespace ChatClient.Tests;

public sealed class RuntimeWorkflowGraphFormattingTests
{
    [Fact]
    public void BuildStageStyle_UsesInvariantDecimalSeparator()
    {
        WithCulture("es-ES", () =>
        {
            var style = RuntimeWorkflowGraphFormatting.BuildStageStyle(
                width: 2640d,
                height: 792d,
                panX: 21.43d,
                panY: 101.64d,
                zoom: 0.2736d);

            Assert.Equal(
                "width:2640px; height:792px; transform: translate(21.43px, 101.64px) scale(0.2736);",
                style);
        });
    }

    [Fact]
    public void BuildHorizontalLinkPath_UsesInvariantDecimalSeparator()
    {
        WithCulture("es-ES", () =>
        {
            var path = RuntimeWorkflowGraphFormatting.BuildHorizontalLinkPath(
                sourceX: 636d,
                sourceY: 76d,
                targetX: 888d,
                targetY: 236d,
                controlOffset: 126d);

            Assert.Equal("M 636 76 C 762 76, 762 236, 888 236", path);
        });
    }

    private static void WithCulture(string cultureName, Action assertion)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            assertion();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
