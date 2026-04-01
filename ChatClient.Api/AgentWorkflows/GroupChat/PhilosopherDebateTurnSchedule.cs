namespace ChatClient.Api.AgentWorkflows.GroupChat;

internal static class PhilosopherDebateTurnSchedule
{
    public static string ResolveSpeakerId(
        int assistantMessageIndex,
        int maximumIterations)
    {
        if (assistantMessageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(assistantMessageIndex));
        }

        if (maximumIterations < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumIterations));
        }

        return assistantMessageIndex switch
        {
            0 => "host",
            1 => "kant",
            2 => "nietzsche",
            var index when index == maximumIterations - 3 => "kant",
            var index when index == maximumIterations - 2 => "nietzsche",
            var index when index == maximumIterations - 1 => "judge",
            var index when index < maximumIterations - 3 => index % 2 == 1 ? "kant" : "nietzsche",
            _ => "judge"
        };
    }
}
