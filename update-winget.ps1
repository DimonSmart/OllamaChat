$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(".\publish\ChatClient.Api.exe")
$version = "$($versionInfo.FileMajorPart).$($versionInfo.FileMinorPart).$($versionInfo.FileBuildPart)"

wingetcreate update DimonSmart.OllamaChat --urls "https://github.com/DimonSmart/OllamaChat/releases/download/v$version/ollama-chat-win-x64-$version.zip" -v $version -s 


