[CmdletBinding()]
param(
    [string]$PackageIdentifier = "DimonSmart.OllamaChat",
    [string]$Source = "winget"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Set-Location $PSScriptRoot

$exePath = Join-Path $PSScriptRoot "publish\ChatClient.Api.exe"
if (-not (Test-Path $exePath)) {
    throw "Missing $exePath. Run .\package-release.ps1 first."
}

$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$version = "{0}.{1}.{2}" -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
$archiveName = "ollama-chat-win-x64-$version.zip"
$url = "https://github.com/DimonSmart/OllamaChat/releases/download/v$version/$archiveName"

wingetcreate update $PackageIdentifier --urls $url -v $version -s $Source
