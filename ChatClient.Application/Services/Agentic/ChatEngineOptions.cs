using System.ComponentModel.DataAnnotations;

namespace ChatClient.Application.Services.Agentic;

public sealed class ChatEngineOptions
{
    public const string SectionName = "ChatEngine";

    [Required]
    public ChatEngineMode Mode { get; set; } = ChatEngineMode.Dual;

    [Required]
    public AgenticToolInvocationPolicyOptions ToolPolicy { get; set; } = new();
}
