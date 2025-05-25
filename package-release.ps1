# Get version from executable and create versioned archive
$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(".\publish\ChatClient.Api.exe")
$version = "$($versionInfo.FileMajorPart).$($versionInfo.FileMinorPart).$($versionInfo.FileBuildPart)"
$archiveName = ".\Winget\ollama-chat-win-x64-$version.zip"

# Ensure Winget directory exists
if (-not (Test-Path -Path ".\Winget")) {
    New-Item -ItemType Directory -Path ".\Winget" | Out-Null
}

# Create the archive with version in filename
Compress-Archive -Path .\publish\* -DestinationPath $archiveName -Force

Write-Host "Created archive: $archiveName with version $version" -ForegroundColor Green
