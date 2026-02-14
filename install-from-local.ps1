[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$KeepInstalled
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Set-Location $PSScriptRoot

$args = @()
if ($SkipPublish) {
    $args += "-SkipPublish"
}

if ($KeepInstalled) {
    $args += "-KeepInstalled"
}

& (Join-Path $PSScriptRoot "package-release.ps1") @args
exit $LASTEXITCODE
