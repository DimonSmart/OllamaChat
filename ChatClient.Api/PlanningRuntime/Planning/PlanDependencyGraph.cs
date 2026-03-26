namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanDependencyGraph
{
    public static IReadOnlyCollection<string> GetDependencies(PlanStep step)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var inputValue in step.In.Values)
        {
            if (!PlanInputBindingSyntax.TryParseBinding(inputValue, out var bindingExpression, out var bindingError)
                || !string.IsNullOrWhiteSpace(bindingError)
                || bindingExpression is null)
            {
                continue;
            }

            foreach (var binding in PlanInputBindingSyntax.EnumerateBindings(bindingExpression))
            {
                if (PlanInputBindingSyntax.TryParseReference(binding.From, out var reference, out _))
                    dependencies.Add(reference!.StepId);
            }
        }

        return dependencies;
    }

    public static Dictionary<string, List<string>> BuildDependentsLookup(IEnumerable<PlanStep> steps)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            dependents.TryAdd(step.Id, []);

            foreach (var dependency in GetDependencies(step))
            {
                if (!dependents.TryGetValue(dependency, out var children))
                {
                    children = [];
                    dependents[dependency] = children;
                }

                children.Add(step.Id);
            }
        }

        return dependents;
    }

    public static IReadOnlyList<string> GetTerminalStepIds(IReadOnlyList<PlanStep> steps)
    {
        var dependentsLookup = BuildDependentsLookup(steps);
        return steps
            .Where(step => !dependentsLookup.TryGetValue(step.Id, out var children) || children.Count == 0)
            .Select(step => step.Id)
            .ToList();
    }
}
