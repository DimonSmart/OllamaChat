// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

// Suppress the experimental API warnings for Ollama and MCP integration
[assembly: SuppressMessage("SemanticKernel", "SKEXP0070", 
    Justification = "Using experimental API as required for Ollama integration")]
[assembly: SuppressMessage("SemanticKernel", "SKEXP0001",
    Justification = "Using experimental API as required for MCP integration")]
