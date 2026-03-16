namespace ChatClient.Domain.Models;

public class FunctionSettings
{
    public int AutoSelectCount { get; set; }

    public List<string> SelectedFunctions { get; set; } = [];

    public bool HasFunctions => AutoSelectCount > 0 || SelectedFunctions.Count > 0;

    public bool IsAutoSelectEnabled => AutoSelectCount > 0;

    public bool HasManualFunctions => !IsAutoSelectEnabled && SelectedFunctions.Count > 0;

    public string GetDisplayText()
    {
        if (AutoSelectCount > 0)
            return AutoSelectCount.ToString();

        if (SelectedFunctions.Count > 0)
            return "\u2713";

        return "\u2212";
    }

    public string GetTooltipText()
    {
        if (AutoSelectCount > 0)
            return $"Auto-select {AutoSelectCount} functions";

        if (SelectedFunctions.Count > 0)
            return $"{SelectedFunctions.Count} functions manually selected";

        return "No functions selected";
    }
}
