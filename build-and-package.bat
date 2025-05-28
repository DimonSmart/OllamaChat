@echo off
REM Build self-contained deployment version for WinGet

echo Cleaning publish folder...
if exist ".\publish" rmdir /s /q ".\publish"

echo Building and publishing OllamaChat (Self-Contained)...
dotnet publish ChatClient.Api\ChatClient.Api.csproj -c Release --self-contained true -r win-x64 -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\publish

echo Creating versioned release archive...
powershell -ExecutionPolicy Bypass -File .\package-release.ps1

echo Done!