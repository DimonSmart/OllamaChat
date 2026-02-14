@echo off
dotnet run --project ChatClient.Api --urls https://localhost:5001 > agentic-check.stdout.log 2> agentic-check.stderr.log
