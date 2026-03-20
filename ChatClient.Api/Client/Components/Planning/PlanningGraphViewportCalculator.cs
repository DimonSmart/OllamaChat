using Blazor.Diagrams.Core.Models;

namespace ChatClient.Api.Client.Components.Planning;

internal readonly record struct PlanningGraphBounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Math.Max(1d, Right - Left);
    public double Height => Math.Max(1d, Bottom - Top);

    public static bool TryCreate(IEnumerable<NodeModel> nodes, out PlanningGraphBounds bounds)
    {
        var materialized = nodes
            .Where(node => node.Size is not null)
            .ToList();

        if (materialized.Count == 0)
        {
            bounds = default;
            return false;
        }

        bounds = new PlanningGraphBounds(
            materialized.Min(node => node.Position.X),
            materialized.Min(node => node.Position.Y),
            materialized.Max(node => node.Position.X + node.Size!.Width),
            materialized.Max(node => node.Position.Y + node.Size!.Height));
        return true;
    }
}

internal readonly record struct PlanningGraphViewportTransform(double Zoom, double PanX, double PanY);

internal static class PlanningGraphViewportCalculator
{
    public static PlanningGraphViewportTransform Fit(
        PlanningGraphBounds bounds,
        double viewportWidth,
        double viewportHeight,
        double padding,
        double minZoom,
        double maxZoom)
    {
        var availableWidth = Math.Max(1d, viewportWidth - padding * 2d);
        var availableHeight = Math.Max(1d, viewportHeight - padding * 2d);
        var zoom = Math.Min(availableWidth / bounds.Width, availableHeight / bounds.Height);

        return Center(bounds, viewportWidth, viewportHeight, zoom, minZoom, maxZoom);
    }

    public static PlanningGraphViewportTransform Center(
        PlanningGraphBounds bounds,
        double viewportWidth,
        double viewportHeight,
        double requestedZoom,
        double minZoom,
        double maxZoom)
    {
        var zoom = Math.Clamp(requestedZoom, minZoom, maxZoom);
        var panX = (viewportWidth - bounds.Width * zoom) / 2d - bounds.Left * zoom;
        var panY = (viewportHeight - bounds.Height * zoom) / 2d - bounds.Top * zoom;

        return new PlanningGraphViewportTransform(zoom, panX, panY);
    }
}
