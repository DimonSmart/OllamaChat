using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public class FunctionSettings
{
    public int AutoSelectCount { get; set; }

    [JsonIgnore]
    public bool IsAutoSelectEnabled => AutoSelectCount > 0;

    public string GetDisplayText()
    {
        if (AutoSelectCount > 0)
            return AutoSelectCount.ToString();

        return "\u2212";
    }

    public string GetTooltipText()
    {
        if (AutoSelectCount > 0)
            return $"Auto-select {AutoSelectCount} tools";

        return "No auto-selected tools";
    }
}
