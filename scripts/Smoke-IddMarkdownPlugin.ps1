param(
    [Parameter(Mandatory = $true)][string]$Agent,
    [Parameter(Mandatory = $true)][string]$Workspace
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../OllamaChat.Cli/OllamaChat.Cli.csproj'
if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) {
    throw "Workspace does not exist: $Workspace"
}
$prompt = 'Use the idd-help skill. Read references/methodology.md and explain the difference between product intent and task history in Intent-Driven Development.'
$answer = & dotnet run --project $project -- run --agent $Agent --workspace $Workspace --prompt $prompt --timeout 10m
if ($LASTEXITCODE -ne 0) {
    throw "Live OllamaChat CLI smoke test failed (exit code $LASTEXITCODE)."
}
if ([string]::IsNullOrWhiteSpace(($answer -join [Environment]::NewLine))) {
    throw 'The model returned an empty response.'
}

$answer
Write-Host ''
Write-Host 'Manual verification: inspect the Harness trace for an idd-help skill load and references/methodology.md resource read.'
