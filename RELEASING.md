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

1. Refresh the vendored indexes, and commit whatever changes:

   ```powershell
   pwsh ./build/Update-VendoredData.ps1    # samples + cmdlet names, from GitHub
   pwsh ./build/Update-CommandIndex.ps1    # search corpus, from the installed PnP.PowerShell
   ```

   Skipping this ships a stale index, and it fails *silently*: `pnp-index.json` still loads and still
   answers, it just describes an older module and says so in a footer nobody reads. Check the printed
   `moduleVersion` is the release you expect. `Update-CommandIndex.ps1` needs `PnP.PowerShell` installed
   locally; `Update-VendoredData.ps1` needs network and, for a higher rate limit, `GITHUB_TOKEN`.

2. Bump `<PackageVersion>` in `PnPPowerShell.MCPServer.csproj` and **both** `version`
   fields in [.mcp/server.json](./.mcp/server.json) — the top-level one and the one under
   `packages[0]`. All three must match.

3. Push a tag:

   ```bash
   git tag v0.1.7-beta
   git push origin v0.1.7-beta
   ```

4. [`release.yml`](./.github/workflows/release.yml) then packs each RID on its own runner,
   verifies that every RID advertised by the wrapper was actually built, and pushes to
   NuGet.org — **RID packages first, wrapper last**, so there is never a window where the
   wrapper resolves to packages that do not exist yet.

5. Finally it publishes [.mcp/server.json](./.mcp/server.json) to the
   [Official MCP Registry](https://registry.modelcontextprotocol.io/). See
   [MCP Registry](#mcp-registry) below.

To do a dry run, use **Actions → Release → Run workflow** with `publish` unchecked: it
builds all eight and uploads them as workflow artifacts without pushing anything.

### Credentials: NuGet trusted publishing

The workflow stores no NuGet API key. The `publish` job uses
[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):
`NuGet/login` exchanges the job's GitHub OIDC token for a NuGet API key that is valid for one
hour. Two things must be in place:

| Where | What |
| --- | --- |
| Repository secret `NUGET_USER` | The nuget.org **username** (profile name, not email address) of the account that created the policy below. |
| nuget.org → your username → **Trusted Publishing** | A policy with Repository Owner `pnp`, Repository `pnp-powershell-mcp-server`, Workflow File `release.yml` (file name only) and Environment left empty. Its owner must be the user or organisation that owns `PnP.PowerShell.MCPServer` and the seven RID packages. |

- **Glob scope.** If you narrow the policy with a package glob, it **must** cover the RID ids
  (`PnP.PowerShell.MCPServer*`). A policy scoped only to the exact id
  `PnP.PowerShell.MCPServer` pushes the wrapper and rejects all seven RID packages, which
  reproduces the original bug.
- **Renaming `release.yml`** breaks the policy.
- **Environment.** Enabling the commented-out `environment:` on the `publish` job needs the
  same environment name in the policy.

The `publish-mcp-registry` job also authenticates with GitHub OIDC, and needs no secret.

## MCP Registry

The server is listed on the [Official MCP Registry](https://registry.modelcontextprotocol.io/)
as `io.github.pnp/pnp-powershell-mcp-server`. It is an upstream source for other catalogues,
such as the [GitHub MCP Registry](https://github.com/mcp). The listing is [.mcp/server.json](./.mcp/server.json). It points at the `PnP.PowerShell.MCPServer`
wrapper package on NuGet.org and does not copy the package, so NuGet.org stays the source of
the binaries.

The `publish-mcp-registry` job in `release.yml` runs after the NuGet push on every tag. It:

1. Stamps the release version into both `version` fields of `server.json`.
2. Waits until NuGet.org has validated the wrapper and serves its README. The registry fetches
   that README itself, so publishing any earlier fails.
3. Logs in with `mcp-publisher login github-oidc`. The GitHub OIDC token grants the
   repository owner's namespace, `io.github.pnp/*`, so this needs no secret, only
   `id-token: write`.
4. Runs `mcp-publisher publish .mcp/server.json`.

### Rules the registry enforces

- **The package README must contain `mcp-name: io.github.pnp/pnp-powershell-mcp-server`.**
  This is how the registry verifies that we own the NuGet package. The line sits as an HTML
  comment near the top of [README.md](./README.md), which is the README packed into the
  wrapper. Do not remove or reword it. NuGet versions are immutable, so a version packed
  without it can never be listed, and the fix is a new version. The `verify` job in
  `release.yml` checks the packed README before anything is pushed, and the
  `mcp-registry-manifest` CI job checks the source README on every PR.
- **`description` is at most 100 characters.** This limit applies to `server.json` only. The
  csproj `<Description>` can be longer.
- **`registryBaseUrl` must be `https://api.nuget.org/v3/index.json`**, or be omitted. The
  `https://api.nuget.org` value that the `mcpserver` template generates is rejected at
  publish time.
- **`$schema` should be the registry's current schema version.** The CI job runs
  `mcp-publisher validate`, which asks the registry to check the file and flags an outdated
  schema.
- **A published version cannot be republished.** To change the listing, release a new
  version.

### Why only two environment variables are declared

VS Code, when it installs from the registry, and the NuGet.org MCP tab both turn every
`environmentVariables` entry that has a `description`, `default` or `choices` into an
install-time prompt. Both ignore `isRequired`. An entry with none of those is written into
the client config as an empty string. Declaring every setting in the README would therefore
ask each new user a series of questions.

`server.json` declares only the two install-time decisions, each as a `false`/`true` pick
that defaults to `false`:

- `PNP_MCP_READONLY`: whether the server may change the tenant.
- `PNP_MCP_ALLOW_SETUP`: whether it may install `PnP.PowerShell` on a machine that lacks it.

The others stay out:

- `PNP_MCP_CONFIRM_DESTRUCTIVE` has only one non-default value, which turns a safety gate
  off. It should not be offered at install.
- The timeout and output cap are tuning settings.
- `PNP_SCRIPT_SAMPLES_PATH` and the record/replay variables are for contributors.

Keep the README configuration table as the full reference.

### Listing a version without a tag push

If the registry step failed after the NuGet push succeeded, use **Re-run failed jobs** on
that workflow run. Otherwise, use **Actions → Release → Run workflow** with `version` set,
`publish` unchecked and `publish_registry` checked. Backfill runs that set only `publish`
never touch the registry.

### Manual fallback

```powershell
# Windows x64; see the registry's latest release for other platforms
curl.exe -L -o mcp-publisher.tar.gz https://github.com/modelcontextprotocol/registry/releases/latest/download/mcp-publisher_windows_amd64.tar.gz
tar xf mcp-publisher.tar.gz
.\mcp-publisher.exe validate .mcp\server.json
.\mcp-publisher.exe login github
.\mcp-publisher.exe publish .mcp\server.json
```

`login github` grants `io.github.pnp/*` only to an **Owner** of the `pnp` GitHub
organisation. Ordinary membership is not enough. With `--token`, the PAT also needs
`read:org`, or Members read-only for a fine-grained token. Anyone else gets only their
personal namespace, and the publish is refused. This is why the workflow uses OIDC.

Check the result at
`https://registry.modelcontextprotocol.io/v0/servers?search=io.github.pnp/pnp-powershell-mcp-server`.

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

Then push the RID packages **before** the wrapper. Trusted publishing works only inside the
workflow, so this needs a personal NuGet.org API key in `NUGET_API_KEY`, with push rights on
`PnP.PowerShell.MCPServer*`. The glob must cover the RID ids, for the same reason as above:

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
