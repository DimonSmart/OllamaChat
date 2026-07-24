namespace ChatClient.Api.AgentWorkflows;

public sealed class ProgrammableGroupChatManagerBuilder
{
    private int _maximumIterations = 40;
    private Func<WorkflowStartValues, int>? _maximumIterationsResolver;
    private GroupChatManagerProgram? _program;
    private string? _programDisplayName;

    internal ProgrammableGroupChatManagerBuilder(
        GroupChatWorkflowManagerDefinition? existing = null)
    {
        if (existing is null)
        {
            return;
        }

        if (existing.MaximumIterations > 0)
        {
            _maximumIterations = existing.MaximumIterations;
        }

        _program = existing.Program;
        _programDisplayName = existing.ProgramDisplayName ?? existing.Program?.DisplayName;
    }

    public ProgrammableGroupChatManagerBuilder MaximumIterations(int maximumIterations)
    {
        if (maximumIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIterations),
                maximumIterations,
                "Maximum iterations must be greater than zero.");
        }

        _maximumIterations = maximumIterations;
        _maximumIterationsResolver = null;
        _program = _program?.WithoutMaximumIterationsResolver();
        return this;
    }

    public ProgrammableGroupChatManagerBuilder MaximumIterations(
        Func<WorkflowStartValues, int> maximumIterationsResolver)
    {
        ArgumentNullException.ThrowIfNull(maximumIterationsResolver);
        _maximumIterationsResolver = maximumIterationsResolver;
        return this;
    }

    public ProgrammableGroupChatManagerBuilder SelectNextSpeaker(
        Func<GroupChatManagerProgramContext, string> selectNextSpeaker)
    {
        ArgumentNullException.ThrowIfNull(selectNextSpeaker);

        _program = new GroupChatManagerProgram(selectNextSpeaker);
        _programDisplayName = null;
        return this;
    }

    public ProgrammableGroupChatManagerBuilder Program(
        GroupChatManagerProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        _program = program;
        _programDisplayName = program.DisplayName;
        return this;
    }

    internal GroupChatWorkflowManagerDefinition Build()
    {
        var program = _maximumIterationsResolver is null || _program is null
            ? _program
            : _program.WithMaximumIterationsResolver(_maximumIterationsResolver);

        return new GroupChatWorkflowManagerDefinition
        {
            Kind = GroupChatWorkflowManagerKind.Programmable,
            MaximumIterations = _maximumIterations,
            Program = program,
            ProgramDisplayName = _programDisplayName
        };
    }
}
