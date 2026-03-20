using ChatClient.Api.Client.Components.Planning;

namespace ChatClient.Tests;

public class PlanningGraphViewportCalculatorTests
{
    [Fact]
    public void Fit_ScalesWideGraphsBelowLegacyFloorAndKeepsBoundsVisible()
    {
        var bounds = new PlanningGraphBounds(32d, -363d, 2362d, 403d);

        var transform = PlanningGraphViewportCalculator.Fit(
            bounds,
            viewportWidth: 720d,
            viewportHeight: 442d,
            padding: 40d,
            minZoom: 0.1d,
            maxZoom: 1.6d);

        Assert.True(transform.Zoom < 0.6d);
        Assert.InRange(transform.Zoom, 0.27d, 0.28d);
        Assert.InRange(bounds.Left * transform.Zoom + transform.PanX, 39.9d, 40.1d);
        Assert.InRange(bounds.Right * transform.Zoom + transform.PanX, 679.9d, 680.1d);
        Assert.True(bounds.Top * transform.Zoom + transform.PanY >= 0d);
        Assert.True(bounds.Bottom * transform.Zoom + transform.PanY <= 442d);
    }

    [Fact]
    public void Center_UsesRequestedZoomWhenItIsWithinAllowedRange()
    {
        var bounds = new PlanningGraphBounds(32d, 48d, 262d, 132d);

        var transform = PlanningGraphViewportCalculator.Center(
            bounds,
            viewportWidth: 720d,
            viewportHeight: 442d,
            requestedZoom: 1d,
            minZoom: 0.1d,
            maxZoom: 1.6d);

        Assert.Equal(1d, transform.Zoom);
        Assert.Equal(213d, transform.PanX);
        Assert.Equal(131d, transform.PanY);
    }
}
