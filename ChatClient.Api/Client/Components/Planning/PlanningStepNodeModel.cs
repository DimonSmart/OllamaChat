using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.PlanningRuntime.Planning;
using Microsoft.AspNetCore.Components.Web;

namespace ChatClient.Api.Client.Components.Planning;

public enum PlanningVisualNodeKind
{
    Step,
    Planning,
    Replanning,
    Result
}

public sealed class PlanningStepNodeModel : NodeModel
{
    public PlanningStepNodeModel(PlanStep step, Point position, bool isActive)
        : base(step.Id, position)
    {
        ControlledSize = true;
        Update(step, isActive, isCollapsed: false, hiddenDescendantCount: 0);
    }

    private PlanningStepNodeModel(string nodeId, Point position)
        : base(nodeId, position)
    {
        ControlledSize = true;
    }

    public static PlanningStepNodeModel CreateVirtual(
        PlanningVirtualNodeDescriptor descriptor,
        Point position,
        bool isActive)
    {
        var node = new PlanningStepNodeModel(descriptor.Id, position);
        node.UpdateVirtual(descriptor, isActive);
        return node;
    }

    public PlanStep? Step { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsCollapsed { get; private set; }

    public int HiddenDescendantCount { get; private set; }

    public PlanningVisualNodeKind VisualKind { get; private set; }

    public string MetaText { get; private set; } = string.Empty;

    public string StatusValue { get; private set; } = PlanStepStatuses.Todo;

    public string StatusText { get; private set; } = "status: todo";

    public Action<PlanningStepNodeModel, PointerEventArgs>? PointerDownRequested { get; set; }

    public Action<PlanningStepNodeModel>? ClickRequested { get; set; }

    public Action<PlanningStepNodeModel>? DoubleClickRequested { get; set; }

    public void Update(PlanStep step, bool isActive, bool isCollapsed, int hiddenDescendantCount)
    {
        Step = step;
        IsActive = isActive;
        IsCollapsed = isCollapsed;
        HiddenDescendantCount = hiddenDescendantCount;
        VisualKind = PlanningVisualNodeKind.Step;
        Title = step.Id;
        MetaText = string.IsNullOrWhiteSpace(step.Tool)
            ? $"llm: {step.Llm ?? string.Empty}"
            : $"tool: {step.Tool}";
        StatusValue = step.Status;
        StatusText = $"status: {step.Status}";
        Refresh();
    }

    public void UpdateVirtual(PlanningVirtualNodeDescriptor descriptor, bool isActive)
    {
        Step = null;
        IsActive = isActive;
        IsCollapsed = false;
        HiddenDescendantCount = 0;
        VisualKind = descriptor.Kind switch
        {
            PlanningVirtualNodeKind.Planning => PlanningVisualNodeKind.Planning,
            PlanningVirtualNodeKind.Replanning => PlanningVisualNodeKind.Replanning,
            PlanningVirtualNodeKind.Result => PlanningVisualNodeKind.Result,
            _ => PlanningVisualNodeKind.Step
        };
        Title = descriptor.Title;
        MetaText = descriptor.Subtitle;
        StatusValue = descriptor.StatusValue;
        StatusText = $"status: {descriptor.StatusValue}";
        Refresh();
    }

    public void RequestPointerDown(PointerEventArgs e) => PointerDownRequested?.Invoke(this, e);

    public void RequestClick() => ClickRequested?.Invoke(this);

    public void RequestDoubleClick() => DoubleClickRequested?.Invoke(this);
}
