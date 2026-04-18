namespace ChatClient.Api.PlanningRuntime.Runtime;

public sealed class RuntimeLlmPromptingOptions
{
    public const string SectionName = "PlanningRuntime:RuntimeLlmPrompting";

    public bool Enabled { get; set; } = true;

    public int SoftPromptChars { get; set; } = 160000;

    public int NormalContentChars { get; set; } = 12000;

    public int NormalHeadChars { get; set; } = 8000;

    public int NormalTailChars { get; set; } = 4000;

    public int RetryContentChars { get; set; } = 4000;

    public int RetryHeadChars { get; set; } = 2500;

    public int RetryTailChars { get; set; } = 1500;

    public RuntimeLlmPromptingOptions CloneNormalized()
    {
        var copy = new RuntimeLlmPromptingOptions
        {
            Enabled = Enabled,
            SoftPromptChars = Math.Max(1, SoftPromptChars),
            NormalContentChars = Math.Max(1, NormalContentChars),
            NormalHeadChars = Math.Max(0, NormalHeadChars),
            NormalTailChars = Math.Max(0, NormalTailChars),
            RetryContentChars = Math.Max(1, RetryContentChars),
            RetryHeadChars = Math.Max(0, RetryHeadChars),
            RetryTailChars = Math.Max(0, RetryTailChars)
        };

        var normalHeadChars = copy.NormalHeadChars;
        var normalTailChars = copy.NormalTailChars;
        NormalizeContentBudget(copy.NormalContentChars, ref normalHeadChars, ref normalTailChars);
        copy.NormalHeadChars = normalHeadChars;
        copy.NormalTailChars = normalTailChars;

        var retryHeadChars = copy.RetryHeadChars;
        var retryTailChars = copy.RetryTailChars;
        NormalizeContentBudget(copy.RetryContentChars, ref retryHeadChars, ref retryTailChars);
        copy.RetryHeadChars = retryHeadChars;
        copy.RetryTailChars = retryTailChars;
        return copy;
    }

    private static void NormalizeContentBudget(int maxContentChars, ref int headChars, ref int tailChars)
    {
        if (headChars + tailChars <= maxContentChars)
            return;

        if (headChars >= maxContentChars)
        {
            headChars = maxContentChars;
            tailChars = 0;
            return;
        }

        tailChars = Math.Max(0, maxContentChars - headChars);
    }
}
