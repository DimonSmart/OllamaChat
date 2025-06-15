using System.Diagnostics.CodeAnalysis;

// Suppress the experimental API warnings for Ollama and MCP integration
[assembly: SuppressMessage("SemanticKernel", "SKEXP0070",
    Justification = "Using experimental API as required for Ollama integration")]
[assembly: SuppressMessage("SemanticKernel", "SKEXP0001",
    Justification = "Using experimental API as required for MCP integration")]
