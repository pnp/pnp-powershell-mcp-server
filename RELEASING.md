# Releasing

## A release is eight packages, not one

This project is packed as a **RID-specific .NET tool**: the csproj sets
`<RuntimeIdentifiers>` (7 RIDs) together with `<SelfContained>`, `<PublishAot>` and
`<PackAsTool>`. That combination makes `dotnet pack` produce a *set* of packages:

| Package | Size | Contents |
| --- | --- | --- |
| `PnP.PowerShell.MCPServer` | ~20 KB | The **wrapper**. No binaries — just `tools/net10.0/any/DotnetToolSettings.xml`, which lists the RID package for each platform. |
| `PnP.PowerShell.MCPServer.win-x64` | ~35–80 MB | The real native executable, at `tools/any/win-x64/PnPPowerShell.MCPServer.exe` |
| `PnP.PowerShell.MCPServer.win-arm64` | " | " |
| `PnP.PowerShell.MCPServer.osx-arm64` | " | " |
| `PnP.PowerShell.MCPServer.osx-x64` | " | " |
| `PnP.PowerShell.MCPServer.linux-x64` | " | " |
| `PnP.PowerShell.MCPServer.linux-arm64` | " | " |
| `PnP.PowerShell.MCPServer.linux-musl-x64` | " | " |

`dotnet tool install --global PnP.PowerShell.MCPServer` resolves the wrapper, reads the
RID list out of `DotnetToolSettings.xml`, and downloads the package matching the user's
machine. **All eight must be on NuGet.org.** If a RID package is missing, users on that
platform get:

```text
Version 0.1.1-beta of package PnP.PowerShell.MCPServer.win-x64 is not found in NuGet feeds ...
```

## The trap

> **`dotnet pack` on its own does NOT build the RID packages, and does not warn you.**

With `<PublishAot>true</PublishAot>`, the SDK deliberately skips the inner per-RID builds
when packing the wrapper, because native AOT **cannot cross-compile between operating
systems**. From `Microsoft.NET.PackTool.props` in the .NET SDK:

```text
* if these builds are RID-specific and AOT, then we pack the outer tool only without implementation dlls
```

So a maintainer running `dotnet pack -c Release` gets exactly one 20 KB package, sees it
succeed, and pushes it. That is how `0.1.1-beta` shipped broken. Each RID package has to be
packed **on its own matching OS**, with `--runtime <rid>`.

## Normal release

1. Bump `<PackageVersion>` in `PnPPowerShell.MCPServer.csproj` and **both** `version`
   fields in [.mcp/server.json](./.mcp/server.json) — the top-level one and the one under
   `packages[0]`. All three must match.
2. Push a tag:

   ```bash
   git tag v0.1.6-beta
   git push origin v0.1.6-beta
   ```

3. [`release.yml`](./.github/workflows/release.yml) then packs each RID on its own runner,
   verifies that every RID advertised by the wrapper was actually built, and pushes to
   NuGet.org — **RID packages first, wrapper last**, so there is never a window where the
   wrapper resolves to packages that do not exist yet.

To do a dry run, use **Actions → Release → Run workflow** with `publish` unchecked: it
builds all eight and uploads them as workflow artifacts without pushing anything.

### Required repository secret

| Secret | Purpose |
| --- | --- |
| `NUGET_API_KEY` | NuGet.org API key with push rights on `PnP.PowerShell.MCPServer*`. Use a glob-scoped key so it also covers the RID package IDs. |

The key's package glob **must** cover the RID ids. A key scoped only to the exact id
`PnP.PowerShell.MCPServer` will push the wrapper and reject all seven RID packages —
reproducing the original bug.

## The broken 0.1.0-beta / 0.1.1-beta releases

Both of those versions published only the 20 KB wrapper, so `dotnet tool install` fails on
every platform. **`0.1.3-beta` is the first release that ships the full set** — that is the
fix, and new installs should use it (`--prerelease` already resolves to the newest).

The two broken versions are still on NuGet.org, and anyone who pins them will still hit the
error. Two follow-ups worth doing:

- **Unlist** `0.1.0-beta` and `0.1.1-beta` on NuGet.org (Manage package → Listing). Unlisting
  hides them from search and from `--prerelease` resolution while leaving existing pins working.
- Optionally **back-fill** them instead: NuGet packages are immutable so the wrapper cannot be
  replaced, but the seven RID package *ids* were never pushed at all, so they can still be
  published under those old versions. Run **Actions → Release → Run workflow** with
  `version` = `0.1.1-beta` and `publish` = checked — the duplicate wrapper push is skipped
  and the seven RID packages go up, repairing that version in place.

## Manual fallback

Only needed if the workflow is unavailable. One machine **cannot** produce all seven —
run the matching command on each OS:

```bash
# on Windows x64 / Windows arm64 (needs VS "Desktop development with C++")
dotnet pack PnPPowerShell.MCPServer.csproj -c Release -r win-x64   -o artifacts
dotnet pack PnPPowerShell.MCPServer.csproj -c Release -r win-arm64 -o artifacts

# on macOS (Xcode command line tools)
dotnet pack PnPPowerShell.MCPServer.csproj -c Release -r osx-arm64 -o artifacts
dotnet pack PnPPowerShell.MCPServer.csproj -c Release -r osx-x64   -o artifacts

# on Linux (clang + zlib1g-dev)
dotnet pack PnPPowerShell.MCPServer.csproj -c Release -r linux-x64   -o artifacts
dotnet pack PnPPowerShell.MCPServer.csproj -c Release -r linux-arm64 -o artifacts

# musl, via the Alpine AOT SDK image
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0-alpine-aot \
  dotnet pack PnPPowerShell.MCPServer.csproj -c Release -r linux-musl-x64 -o artifacts

# the RID-agnostic wrapper (any machine)
dotnet pack PnPPowerShell.MCPServer.csproj -c Release -o artifacts
```

Then push the RID packages **before** the wrapper:

```bash
dotnet nuget push "artifacts/PnP.PowerShell.MCPServer.*-*.nupkg" \
  -s https://api.nuget.org/v3/index.json -k "$NUGET_API_KEY" --skip-duplicate
dotnet nuget push "artifacts/PnP.PowerShell.MCPServer.<version>.nupkg" \
  -s https://api.nuget.org/v3/index.json -k "$NUGET_API_KEY" --skip-duplicate
```

## Verifying a release

```bash
dotnet tool install --global PnP.PowerShell.MCPServer --prerelease
pnp-powershell-mcp-server --help
```

Or check the feed directly — this must return `200`, not `404`:

```bash
curl -sI https://api.nuget.org/v3-flatcontainer/PnP.PowerShell.MCPServer.win-x64/index.json
```
