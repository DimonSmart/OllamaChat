using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Events;
using Blazor.Diagrams.Core.Models.Base;

namespace ChatClient.Api.Client.Components.Planning;

public sealed class PlanningSelectionBehavior : Behavior
{
    private readonly Func<bool> _isPanModifierActive;

    public PlanningSelectionBehavior(Diagram diagram, Func<bool> isPanModifierActive)
        : base(diagram)
    {
        _isPanModifierActive = isPanModifierActive;
        Diagram.PointerDown += OnPointerDown;
    }

    public override void Dispose()
    {
        Diagram.PointerDown -= OnPointerDown;
    }

    private void OnPointerDown(Model? model, PointerEventArgs e)
    {
        var ctrlKey = e.CtrlKey;

        if (model is SelectableModel selectable)
        {
            if (ctrlKey && selectable.Selected)
            {
                Diagram.UnselectModel(selectable);
                return;
            }

            if (!selectable.Selected)
            {
                Diagram.SelectModel(selectable, !ctrlKey || !Diagram.Options.AllowMultiSelection);
            }

            return;
        }

        if (_isPanModifierActive())
            return;

        Diagram.UnselectAll();
    }
}
