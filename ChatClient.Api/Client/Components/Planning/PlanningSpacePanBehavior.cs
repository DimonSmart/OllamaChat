using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Events;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models.Base;

namespace ChatClient.Api.Client.Components.Planning;

public sealed class PlanningSpacePanBehavior : Behavior
{
    private readonly Func<bool> _isPanModifierActive;
    private Point? _initialPan;
    private double _lastClientX;
    private double _lastClientY;

    public PlanningSpacePanBehavior(Diagram diagram, Func<bool> isPanModifierActive)
        : base(diagram)
    {
        _isPanModifierActive = isPanModifierActive;
        Diagram.PointerDown += OnPointerDown;
        Diagram.PointerMove += OnPointerMove;
        Diagram.PointerUp += OnPointerUp;
    }

    public override void Dispose()
    {
        Diagram.PointerDown -= OnPointerDown;
        Diagram.PointerMove -= OnPointerMove;
        Diagram.PointerUp -= OnPointerUp;
    }

    private void OnPointerDown(Model? model, PointerEventArgs e)
    {
        if (e.Button != 0L || model is not null || !_isPanModifierActive() || !Diagram.Options.AllowPanning)
            return;

        _initialPan = Diagram.Pan;
        _lastClientX = e.ClientX;
        _lastClientY = e.ClientY;
    }

    private void OnPointerMove(Model? model, PointerEventArgs e)
    {
        if (!Diagram.Options.AllowPanning || _initialPan is null)
            return;

        var deltaX = e.ClientX - _lastClientX - (Diagram.Pan.X - _initialPan.X);
        var deltaY = e.ClientY - _lastClientY - (Diagram.Pan.Y - _initialPan.Y);
        Diagram.UpdatePan(deltaX, deltaY);
    }

    private void OnPointerUp(Model? model, PointerEventArgs e)
    {
        _initialPan = null;
    }
}
