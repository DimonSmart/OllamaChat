using System.Globalization;

namespace ChatClient.Api.Client.Components.Planning;

internal static class RuntimeWorkflowGraphFormatting
{
    public static string BuildStageStyle(double width, double height, double panX, double panY, double zoom) =>
        FormattableString.Invariant(
            $"width:{width:0.####}px; height:{height:0.####}px; transform: translate({panX:0.####}px, {panY:0.####}px) scale({zoom:0.####});");

    public static string BuildCanvasStyle(double width, double height) =>
        FormattableString.Invariant($"width:{width:0.####}px; height:{height:0.####}px;");

    public static string BuildNodeStyle(double x, double y) =>
        FormattableString.Invariant($"left:{x:0.####}px; top:{y:0.####}px;");

    public static string BuildHorizontalLinkPath(
        double sourceX,
        double sourceY,
        double targetX,
        double targetY,
        double controlOffset)
    {
        var sourceControlX = sourceX + controlOffset;
        var targetControlX = targetX - controlOffset;

        return BuildCubicBezierPath(
            sourceX,
            sourceY,
            sourceControlX,
            sourceY,
            targetControlX,
            targetY,
            targetX,
            targetY);
    }

    public static string BuildVerticalLinkPath(
        double sourceX,
        double sourceY,
        double targetX,
        double targetY,
        double controlOffset)
    {
        var sourceControlY = sourceY + controlOffset;
        var targetControlY = targetY - controlOffset;

        return BuildCubicBezierPath(
            sourceX,
            sourceY,
            sourceX,
            sourceControlY,
            targetX,
            targetControlY,
            targetX,
            targetY);
    }

    private static string BuildCubicBezierPath(
        double startX,
        double startY,
        double control1X,
        double control1Y,
        double control2X,
        double control2Y,
        double endX,
        double endY) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"M {startX:0.###} {startY:0.###} C {control1X:0.###} {control1Y:0.###}, {control2X:0.###} {control2Y:0.###}, {endX:0.###} {endY:0.###}");
}
