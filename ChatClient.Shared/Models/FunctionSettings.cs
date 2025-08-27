namespace ChatClient.Shared.Models;

/// <summary>
/// Represents function selection settings for an agent.
/// Contains both auto-selection count and manually selected functions.
/// </summary>
public class FunctionSettings
{
    /// <summary>
    /// Number of functions to auto-select. When > 0, manual selection is disabled.
    /// </summary>
    public int AutoSelectCount { get; set; }

    /// <summary>
    /// List of manually selected function names.
    /// These are ignored when AutoSelectCount > 0.
    /// </summary>
    public List<string> SelectedFunctions { get; set; } = [];

    /// <summary>
    /// Indicates whether any functions are configured (auto or manual).
    /// </summary>
    public bool HasFunctions => AutoSelectCount > 0 || SelectedFunctions.Count > 0;

    /// <summary>
    /// Indicates whether auto-selection is enabled.
    /// </summary>
    public bool IsAutoSelectEnabled => AutoSelectCount > 0;

    /// <summary>
    /// Indicates whether manual functions are selected.
    /// </summary>
    public bool HasManualFunctions => !IsAutoSelectEnabled && SelectedFunctions.Count > 0;

    /// <summary>
    /// Gets display text for the function settings.
    /// Returns auto-select count, checkmark for manual functions, or dash for none.
    /// </summary>
    public string GetDisplayText()
    {
        if (AutoSelectCount > 0)
            return AutoSelectCount.ToString();

        if (SelectedFunctions.Count > 0)
            return "✓";

        return "−";
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
