# Launch this script from C:\Private\OllamaChat

# Ensure the working directory is the script folder
try { Set-Location $PSScriptRoot } catch { }
Write-Host "Working directory: $(Get-Location)"

# 0. Read the version from publish\ChatClient.Api.exe
$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(".\publish\ChatClient.Api.exe")
$version     = "$($versionInfo.FileMajorPart).$($versionInfo.FileMinorPart).$($versionInfo.FileBuildPart)"
Write-Host "Detected version: $version"

# 1. Define relative paths (from script root)
$manifestPath     = "manifests\d\DimonSmart\OllamaChat\$version\DimonSmart.OllamaChat.installer.yaml"
$manifestDirLocal = Split-Path $manifestPath -Parent
$absoluteManifestDir = (Resolve-Path $manifestDirLocal).Path + '\'
$backupPath       = "$manifestPath.bak"
$relativeZip      = "winget\ollama-chat-win-x64-$version.zip"

Write-Host "Manifest file path (relative): $manifestPath"
Write-Host "Manifest directory (relative): $manifestDirLocal"
Write-Host "Manifest directory (absolute): $absoluteManifestDir"
Write-Host "Backup path:                  $backupPath"
Write-Host "Local ZIP path:               $relativeZip"

# 2. Replace InstallerUrl line with local ZIP path prefixed with file://
$pattern     = '^(\s*InstallerUrl:).*'
# Build replacement: group 1 (InstallerUrl:) plus space plus quoted file URI
$fileUri     = 'file:///' + $relativeZip.Replace('\\','/')
$replacement = '$1 "' + $fileUri + '"'
Write-Host "Using regex pattern: $pattern"
Write-Host "Replacement string:   $replacement"

# 3. Backup the original manifest
Write-Host "Backing up original manifest..."
Copy-Item -Path $manifestPath -Destination $backupPath -Force

try {
    # 4. Patch the InstallerUrl to point to the local file URI
    Write-Host "Patching InstallerUrl in manifest..."
    (Get-Content $manifestPath) |
      ForEach-Object { $_ -replace $pattern, $replacement } |
      Set-Content   $manifestPath

    # Diagnostic: print the new InstallerUrl line
    $newLine = Get-Content $manifestPath | Where-Object { $_ -match '^(\s*InstallerUrl:)' }
    Write-Host "New InstallerUrl line: $newLine"

    # 5. Install using winget pointing to the manifest directory
    Write-Host "Running: winget install --manifest $absoluteManifestDir"
    winget install --manifest "$absoluteManifestDir"
}
finally {
    # 6. Restore the original manifest
    Write-Host "Restoring original manifest..."
    Move-Item -Path $backupPath -Destination $manifestPath -Force
    Write-Host "Done."
}
