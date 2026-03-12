using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using ChatClient.Api.PlanningRuntime.Planning;
using Microsoft.AspNetCore.Components.Web;

namespace ChatClient.Api.Client.Components.Planning;

public sealed class PlanningStepNodeModel : NodeModel
{
    public PlanningStepNodeModel(PlanStep step, Point position, bool isActive)
        : base(step.Id, position)
    {
        ControlledSize = true;
        Step = step;
        IsActive = isActive;
        Title = step.Id;
    }

    public PlanStep Step { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsCollapsed { get; private set; }

    public int HiddenDescendantCount { get; private set; }

    public Action<PlanningStepNodeModel, PointerEventArgs>? PointerDownRequested { get; set; }

    public Action<PlanningStepNodeModel>? ClickRequested { get; set; }

    public Action<PlanningStepNodeModel>? DoubleClickRequested { get; set; }

    public void Update(PlanStep step, bool isActive, bool isCollapsed, int hiddenDescendantCount)
    {
        Step = step;
        IsActive = isActive;
        IsCollapsed = isCollapsed;
        HiddenDescendantCount = hiddenDescendantCount;
        Refresh();
    }

    public void RequestPointerDown(PointerEventArgs e) => PointerDownRequested?.Invoke(this, e);

    public void RequestClick() => ClickRequested?.Invoke(this);

    public void RequestDoubleClick() => DoubleClickRequested?.Invoke(this);
}
