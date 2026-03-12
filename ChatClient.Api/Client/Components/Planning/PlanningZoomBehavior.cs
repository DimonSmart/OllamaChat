using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Events;

namespace ChatClient.Api.Client.Components.Planning;

public sealed class PlanningZoomBehavior : Behavior
{
    public PlanningZoomBehavior(Diagram diagram)
        : base(diagram)
    {
        Diagram.Wheel += OnWheel;
    }

    public override void Dispose()
    {
        Diagram.Wheel -= OnWheel;
    }

    private void OnWheel(WheelEventArgs e)
    {
        if (Diagram.Container is null ||
            e.DeltaY == 0d ||
            !e.CtrlKey ||
            !Diagram.Options.Zoom.Enabled)
        {
            return;
        }

        var zoom = Diagram.Zoom;
        var scaleFactor = Diagram.Options.Zoom.ScaleFactor;
        var direction = Diagram.Options.Zoom.Inverse ? e.DeltaY * -1d : e.DeltaY;
        var newZoom = direction > 0d ? zoom * scaleFactor : zoom / scaleFactor;
        newZoom = Math.Clamp(newZoom, Diagram.Options.Zoom.Minimum, Diagram.Options.Zoom.Maximum);

        if (Math.Abs(newZoom - zoom) < 0.0001d)
            return;

        var container = Diagram.Container;
        var widthDelta = container.Width * newZoom - container.Width * zoom;
        var heightDelta = container.Height * newZoom - container.Height * zoom;
        var relativeX = e.ClientX - container.Left;
        var relativeY = e.ClientY - container.Top;
        var ratioX = (relativeX - Diagram.Pan.X) / zoom / container.Width;
        var ratioY = (relativeY - Diagram.Pan.Y) / zoom / container.Height;
        var newPanX = Diagram.Pan.X - widthDelta * ratioX;
        var newPanY = Diagram.Pan.Y - heightDelta * ratioY;

        Diagram.Batch(() =>
        {
            Diagram.SetPan(newPanX, newPanY);
            Diagram.SetZoom(newZoom);
        });
    }
}
