[CmdletBinding()]
param(
    [string]$ProjectPath = "ChatClient.Api/ChatClient.Api.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$PackageIdentifier = "DimonSmart.OllamaChat",
    [string]$PackageName = "DimonSmart.OllamaChat",
    [string]$PortableCommandAlias = "DimonSmart.OllamaChat",
    [string]$Publisher = "DimonSmart",
    [string]$PublisherUrl = "https://github.com/DimonSmart",
    [string]$PublisherSupportUrl = "https://github.com/DimonSmart/OllamaChat/issues",
    [string]$PackageUrl = "https://github.com/DimonSmart/OllamaChat",
    [string]$DocumentationUrl = "https://github.com/DimonSmart/OllamaChat/wiki",
    [string]$ReleaseBaseUrl = "https://github.com/DimonSmart/OllamaChat/releases/download",
    [string]$ShortDescription = "C# based Ollama chat with MCP support",
    [switch]$SkipPublish,
    [switch]$SkipSmokeTest,
    [switch]$KeepInstalled
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Invoke-NativeCommand {
    param(
        [string]$Command,
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $Command $($Arguments -join ' ')"
    }
}

function Get-ArchitectureFromRuntime {
    param([string]$RuntimeIdentifier)

    if ($RuntimeIdentifier -match "arm64") {
        return "arm64"
    }

    if ($RuntimeIdentifier -match "x86") {
        return "x86"
    }

    return "x64"
}

function Get-ManifestDirectory {
    param(
        [string]$ManifestPackageIdentifier,
        [string]$ManifestPackageVersion
    )

    $parts = $ManifestPackageIdentifier.Split(".")
    if ($parts.Length -lt 2) {
        throw "PackageIdentifier '$ManifestPackageIdentifier' must contain at least one dot."
    }

    $publisherSegment = $parts[0]
    $packageSegment = ($parts[1..($parts.Length - 1)] -join ".")
    $firstLetter = $publisherSegment.Substring(0, 1).ToLowerInvariant()

    return Join-Path $PSScriptRoot ("manifests/{0}/{1}/{2}/{3}" -f $firstLetter, $publisherSegment, $packageSegment, $ManifestPackageVersion)
}

function Test-PackageInstalled {
    param([string]$ManifestPackageIdentifier)

    $listOutput = & winget list --id $ManifestPackageIdentifier --exact --accept-source-agreements --disable-interactivity 2>&1 | Out-String
    return $listOutput -match [System.Text.RegularExpressions.Regex]::Escape($ManifestPackageIdentifier)
}

Set-Location $PSScriptRoot

$publishDirectory = Join-Path $PSScriptRoot "publish"
$wingetArtifactsDirectory = Join-Path $PSScriptRoot "winget"

if (-not (Test-Path $wingetArtifactsDirectory)) {
    New-Item -Path $wingetArtifactsDirectory -ItemType Directory | Out-Null
}

if (-not $SkipPublish) {
    Write-Section "Publishing"
    if (Test-Path $publishDirectory) {
        Remove-Item -Path $publishDirectory -Recurse -Force
    }

    Invoke-NativeCommand -Command "dotnet" -Arguments @(
        "publish",
        $ProjectPath,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:PublishTrimmed=false",
        "-o", $publishDirectory
    )
}

$projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
$publishedExecutable = Join-Path $publishDirectory "$projectName.exe"

if (-not (Test-Path $publishedExecutable)) {
    throw "Published executable not found: $publishedExecutable. Build first or remove -SkipPublish."
}

$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExecutable)
$packageVersion = "{0}.{1}.{2}" -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
$archiveFileName = "ollama-chat-{0}-{1}.zip" -f $Runtime, $packageVersion
$archivePath = Join-Path $wingetArtifactsDirectory $archiveFileName

Write-Section "Creating Archive"
if (Test-Path $archivePath) {
    Remove-Item -Path $archivePath -Force
}

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -Force
$archiveSha256 = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()

Write-Host "Version: $packageVersion"
Write-Host "Archive: $archivePath"
Write-Host "SHA256: $archiveSha256"

$releaseDate = Get-Date -Format "yyyy-MM-dd"
$releaseTag = "v$packageVersion"
$releaseInstallerUrl = "$ReleaseBaseUrl/$releaseTag/$archiveFileName"
$releaseNotesUrl = "$PackageUrl/releases/tag/$releaseTag"
$manifestDirectory = Get-ManifestDirectory -ManifestPackageIdentifier $PackageIdentifier -ManifestPackageVersion $packageVersion

Write-Section "Generating Winget Manifests"
if (Test-Path $manifestDirectory) {
    Remove-Item -Path $manifestDirectory -Recurse -Force
}
New-Item -Path $manifestDirectory -ItemType Directory | Out-Null

$versionManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.yaml"
$installerManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.installer.yaml"
$localeManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.locale.en-US.yaml"
$architecture = Get-ArchitectureFromRuntime -RuntimeIdentifier $Runtime

$versionManifest = @"
# Generated by package-release.ps1
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.9.0.schema.json

PackageIdentifier: $PackageIdentifier
PackageVersion: $packageVersion
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.9.0
"@

$installerManifest = @"
# Generated by package-release.ps1
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.9.0.schema.json

PackageIdentifier: $PackageIdentifier
PackageVersion: $packageVersion
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: $projectName.exe
  PortableCommandAlias: $PortableCommandAlias
ArchiveBinariesDependOnPath: false
Installers:
- Architecture: $architecture
  InstallerUrl: $releaseInstallerUrl
  InstallerSha256: $archiveSha256
ManifestType: installer
ManifestVersion: 1.9.0
ReleaseDate: $releaseDate
"@

$localeManifest = @"
# Generated by package-release.ps1
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.9.0.schema.json

PackageIdentifier: $PackageIdentifier
PackageVersion: $packageVersion
PackageLocale: en-US
Publisher: $Publisher
PublisherUrl: $PublisherUrl
PublisherSupportUrl: $PublisherSupportUrl
PackageName: $PackageName
PackageUrl: $PackageUrl
License: MIT License
ShortDescription: $ShortDescription
ReleaseNotesUrl: $releaseNotesUrl
Documentations:
- DocumentLabel: Wiki
  DocumentUrl: $DocumentationUrl
ManifestType: defaultLocale
ManifestVersion: 1.9.0
"@

Write-Utf8NoBom -Path $versionManifestPath -Content $versionManifest
Write-Utf8NoBom -Path $installerManifestPath -Content $installerManifest
Write-Utf8NoBom -Path $localeManifestPath -Content $localeManifest

Write-Host "Manifest directory: $manifestDirectory"

Write-Section "Validating Release Manifests"
Invoke-NativeCommand -Command "winget" -Arguments @(
    "validate",
    "--manifest", $manifestDirectory,
    "--disable-interactivity"
)

$localManifestDirectory = $null
$artifactServerProcess = $null
$smokeProcess = $null
$stdoutTask = $null
$stderrTask = $null
$smokeLogPath = Join-Path $wingetArtifactsDirectory ("smoke-test-{0}.log" -f $packageVersion)
$wasInstalledBefore = $false
$installCompleted = $false

try {
    if (-not $SkipSmokeTest) {
        Write-Section "Preparing Local Manifest For Install Test"
        $localManifestDirectory = Join-Path $env:TEMP ("winget-local-{0}" -f [Guid]::NewGuid().ToString("N"))
        New-Item -Path $localManifestDirectory -ItemType Directory | Out-Null
        Copy-Item -Path (Join-Path $manifestDirectory "*") -Destination $localManifestDirectory -Force

        $localInstallerManifestPath = Join-Path $localManifestDirectory "$PackageIdentifier.installer.yaml"
        $artifactServerPort = Get-Random -Minimum 8100 -Maximum 8999
        $archiveUri = "http://127.0.0.1:$artifactServerPort/$archiveFileName"

        $artifactServerStartInfo = New-Object System.Diagnostics.ProcessStartInfo
        $artifactServerStartInfo.FileName = "python"
        $artifactServerStartInfo.Arguments = "-m http.server $artifactServerPort --bind 127.0.0.1 --directory `"$wingetArtifactsDirectory`""
        $artifactServerStartInfo.WorkingDirectory = $wingetArtifactsDirectory
        $artifactServerStartInfo.UseShellExecute = $false
        $artifactServerStartInfo.CreateNoWindow = $true
        $artifactServerStartInfo.RedirectStandardOutput = $true
        $artifactServerStartInfo.RedirectStandardError = $true

        $artifactServerProcess = New-Object System.Diagnostics.Process
        $artifactServerProcess.StartInfo = $artifactServerStartInfo
        $null = $artifactServerProcess.Start()
        Start-Sleep -Seconds 1

        if ($artifactServerProcess.HasExited) {
            throw "Failed to start local artifact HTTP server."
        }

        $localInstallerLines = Get-Content -Path $localInstallerManifestPath
        $localInstallerLines = $localInstallerLines -replace '^(\s*InstallerUrl:).*$', ('$1 "' + $archiveUri + '"')
        Write-Utf8NoBom -Path $localInstallerManifestPath -Content (($localInstallerLines -join [Environment]::NewLine) + [Environment]::NewLine)

        Invoke-NativeCommand -Command "winget" -Arguments @(
            "validate",
            "--manifest", $localManifestDirectory,
            "--disable-interactivity"
        )

        Write-Section "Installing From Local Manifest"
        $wasInstalledBefore = Test-PackageInstalled -ManifestPackageIdentifier $PackageIdentifier
        Invoke-NativeCommand -Command "winget" -Arguments @(
            "install",
            "--manifest", $localManifestDirectory,
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity",
            "--silent",
            "--force",
            "--ignore-local-archive-malware-scan"
        )
        $installCompleted = $true

        Write-Section "Smoke Testing Installed Package"
        $commandLinkPath = Join-Path $env:LOCALAPPDATA ("Microsoft\WinGet\Links\{0}.exe" -f $PortableCommandAlias)
        if (-not (Test-Path $commandLinkPath)) {
            throw "Portable command link not found: $commandLinkPath"
        }

        $smokePort = Get-Random -Minimum 5200 -Maximum 5800
        $smokeBaseUrl = "http://127.0.0.1:$smokePort"

        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $commandLinkPath
        $startInfo.Arguments = "--urls $smokeBaseUrl"
        $startInfo.WorkingDirectory = $env:TEMP
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.EnvironmentVariables["OLLAMACHAT_DISABLE_BROWSER_LAUNCH"] = "1"

        $smokeProcess = New-Object System.Diagnostics.Process
        $smokeProcess.StartInfo = $startInfo
        $null = $smokeProcess.Start()

        $stdoutTask = $smokeProcess.StandardOutput.ReadToEndAsync()
        $stderrTask = $smokeProcess.StandardError.ReadToEndAsync()

        $apiReady = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            if ($smokeProcess.HasExited) {
                break
            }

            try {
                $apiResponse = Invoke-WebRequest -Uri "$smokeBaseUrl/api" -UseBasicParsing -TimeoutSec 2
                if ($apiResponse.StatusCode -eq 200) {
                    $apiReady = $true
                    break
                }
            }
            catch {
                Start-Sleep -Seconds 1
            }
        }

        if (-not $apiReady) {
            throw "Smoke test failed: /api endpoint did not respond on $smokeBaseUrl"
        }

        $rootResponse = Invoke-WebRequest -Uri "$smokeBaseUrl/" -UseBasicParsing -TimeoutSec 5
        if ($rootResponse.StatusCode -lt 200 -or $rootResponse.StatusCode -ge 400) {
            throw "Smoke test failed: root endpoint returned HTTP $($rootResponse.StatusCode)"
        }

        Write-Host "Smoke test succeeded at $smokeBaseUrl" -ForegroundColor Green
    }
}
finally {
    if ($smokeProcess -and -not $smokeProcess.HasExited) {
        $smokeProcess.Kill()
        $smokeProcess.WaitForExit(5000) | Out-Null
    }

    if ($stdoutTask -or $stderrTask) {
        $stdout = ""
        $stderr = ""
        if ($stdoutTask) {
            $stdout = $stdoutTask.GetAwaiter().GetResult()
        }

        if ($stderrTask) {
            $stderr = $stderrTask.GetAwaiter().GetResult()
        }

        $combinedLog = @"
STDOUT:
$stdout

STDERR:
$stderr
"@
        Write-Utf8NoBom -Path $smokeLogPath -Content $combinedLog
        Write-Host "Smoke log: $smokeLogPath"
    }

    if ($artifactServerProcess -and -not $artifactServerProcess.HasExited) {
        $artifactServerProcess.Kill()
        $artifactServerProcess.WaitForExit(3000) | Out-Null
    }

    if ((-not $SkipSmokeTest) -and $installCompleted -and (-not $KeepInstalled) -and (-not $wasInstalledBefore)) {
        Write-Section "Cleaning Up Test Install"
        $uninstallOutput = & winget uninstall --manifest $localManifestDirectory --accept-source-agreements --disable-interactivity --silent --force 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -and $uninstallOutput -notmatch "No installed package found matching input criteria") {
            throw "Cleanup uninstall failed: $uninstallOutput"
        }
    }

    if ($localManifestDirectory -and (Test-Path $localManifestDirectory)) {
        Remove-Item -Path $localManifestDirectory -Recurse -Force
    }
}

Write-Section "Done"
Write-Host "Archive ready: $archivePath" -ForegroundColor Green
Write-Host "Manifest ready: $manifestDirectory" -ForegroundColor Green
