@echo off
REM filepath: c:\Private\NugetMcpServer\build-and-package.bat

echo Building and publishing NugetMcpServer...
dotnet publish ChatClient.Api\ChatClient.Api.csproj -c Release -p:PublishTrimmed=false -o .\publish

echo Creating versioned release archive...
powershell -ExecutionPolicy Bypass -File .\package-release.ps1

echo Done!