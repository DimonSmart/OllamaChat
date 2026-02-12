using System.ComponentModel.DataAnnotations;

namespace ChatClient.Application.Services.Agentic;

public sealed class ChatEngineOptions
{
    public const string SectionName = "ChatEngine";

    [Required]
    public AgenticToolInvocationPolicyOptions ToolPolicy { get; set; } = new();
}
