using Blazor.Diagrams.Core.Models;

namespace ChatClient.Api.Client.Components.Planning;

public sealed class PlanningGraphLinkModel : LinkModel
{
    public PlanningGraphLinkModel(PlanningGraphLinkDescriptor descriptor, PlanningStepNodeModel source, PlanningStepNodeModel target)
        : base(descriptor.Id, source, target)
    {
        Descriptor = descriptor;
    }

    public PlanningGraphLinkDescriptor Descriptor { get; }

    public Action<PlanningGraphLinkModel>? ClickRequested { get; set; }

    public void RequestClick() => ClickRequested?.Invoke(this);
}
