# Project Verification

```yaml
version: 1

checks:
  solution-build:
    run: dotnet build OllamaChat.sln --no-restore -m:1
    timeout: 2m
  unit-tests:
    run: dotnet test ChatClient.Tests/ChatClient.Tests.csproj --no-restore -m:1 --filter "Category!=RealWebExploration"
    timeout: 5m
  diff-check:
    run: git diff --check
    timeout: 30s

default:
  use:
    - solution-build
    - unit-tests
    - diff-check
```

`unit-tests` deliberately excludes the `RealWebExploration` trait. xUnit tests
marked `Explicit = true`, including real workflow execution, are not selected by
the normal test command and therefore are not Factory verification.
