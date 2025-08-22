# OllamaChat

OllamaChat is a sample chat client for local LLMs built on
[Semantic Kernel](https://github.com/microsoft/semantic-kernel). It supports
multi‑agent conversations via `GroupChatOrchestration` and uses a
`AppForceLastUserReducer` so the chat history always ends with a user message
(`AuthorName="user"`).

## Running Tests

Integration tests that talk to an Ollama server are disabled by default. To run them:

1. Start an Ollama server on `http://localhost:11434`.
2. Set `RUN_OLLAMA_TESTS=1` in the environment.
3. Execute `dotnet test ChatClient.Tests/ChatClient.Tests.csproj`.

If the server is unavailable, the test is skipped automatically.
