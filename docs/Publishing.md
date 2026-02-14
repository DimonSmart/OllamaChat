### WinGet Packaging

Repository already contains a full packaging flow in `package-release.ps1`.

It performs:

1. `dotnet publish` self-contained single-file build (`win-x64`);
2. ZIP creation in `winget/`;
3. WinGet manifests generation in `manifests/d/DimonSmart/OllamaChat/<version>/`;
4. `winget validate` on generated manifests;
5. local install smoke-test through `winget install --manifest` and endpoint checks.

### Run Full Flow

```powershell
.\package-release.ps1
```

or:

```bat
build-and-package.bat
```

### Useful Flags

```powershell
# Reuse existing publish output
.\package-release.ps1 -SkipPublish

# Skip local install smoke-test (build/package/validate only)
.\package-release.ps1 -SkipSmokeTest

# Keep package installed after smoke-test
.\package-release.ps1 -KeepInstalled
```

### Local Install Shortcut

`install-from-local.ps1` is a thin wrapper over `package-release.ps1`:

```powershell
.\install-from-local.ps1 -SkipPublish
```

### Notes

- If `winget install --manifest` is blocked on your machine, enable local manifests (requires elevated shell):

```powershell
winget settings --enable LocalManifestFiles
```

- For release publication, upload generated ZIP to GitHub Release `v<version>` and commit generated manifest folder.
