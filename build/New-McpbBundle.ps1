#Requires -Version 7.4
<#
.SYNOPSIS
Builds the Claude Desktop bundle (.mcpb) from the RID packages a release has already packed.

.DESCRIPTION
The bundle carries the native executables themselves, so installing it needs neither the .NET SDK nor
`dotnet tool install`. It takes them out of the RID .nupkg files rather than building again, so the
bundle and the NuGet packages of one release hold the same binaries. Needs Node.js for `npx`.

.EXAMPLE
./build/New-McpbBundle.ps1 -PackageDirectory artifacts -Version 0.1.8-beta
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $Version,
    [string] $Output = $PackageDirectory,
    # Claude Desktop runs on Windows and macOS only. Narrow this for a local test build.
    [string[]] $Rids = @('win-x64', 'osx-arm64', 'osx-x64'),
    [string] $McpbVersion = '2.1.2'
)

$ErrorActionPreference = 'Stop'

if ($Rids -notcontains 'win-x64') { throw 'The manifest starts win-x64 on Windows, so the bundle needs it.' }

$mac = @($Rids | Where-Object { $_.StartsWith('osx') })
if ($mac.Count -notin 0, 2) { throw 'The macOS launcher chooses between osx-arm64 and osx-x64, so the bundle needs both or neither.' }

$root = Join-Path $PSScriptRoot '..'
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "pnp-mcpb-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage | Out-Null

try {
    foreach ($rid in $Rids) {
        $package = Get-ChildItem $PackageDirectory -Filter "PnP.PowerShell.MCPServer.$rid.$Version.nupkg" | Select-Object -First 1
        if (-not $package) { throw "No package for $rid $Version in '$PackageDirectory'." }

        $unpacked = Join-Path $stage "_$rid"
        Expand-Archive -LiteralPath $package.FullName -DestinationPath $unpacked

        $name = $rid.StartsWith('win') ? 'PnPPowerShell.MCPServer.exe' : 'PnPPowerShell.MCPServer'
        $binary = Get-ChildItem (Join-Path $unpacked 'tools') -Recurse -File -Filter $name | Select-Object -First 1
        if (-not $binary) { throw "$($package.Name) contains no $name." }

        $target = New-Item -ItemType Directory -Path (Join-Path $stage 'server' $rid) -Force
        Copy-Item $binary.FullName $target
        Remove-Item $unpacked -Recurse -Force
    }

    $manifest = Get-Content (Join-Path $root 'mcpb' 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    $manifest.version = $Version

    if ($mac.Count -eq 2) {
        Copy-Item (Join-Path $root 'mcpb' 'launch.sh') (Join-Path $stage 'server')
    }
    else {
        $manifest.server.mcp_config.Remove('platform_overrides')
        $manifest.compatibility.platforms = @('win32')
    }

    $manifest | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    Copy-Item (Join-Path $root 'assets' 'parker.png') $stage

    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    $bundle = Join-Path (Resolve-Path $Output) "pnp-powershell-$Version.mcpb"

    npx --yes "@anthropic-ai/mcpb@$McpbVersion" validate (Join-Path $stage 'manifest.json')
    if ($LASTEXITCODE -ne 0) { throw 'The bundle manifest is not valid.' }

    npx --yes "@anthropic-ai/mcpb@$McpbVersion" pack $stage $bundle
    if ($LASTEXITCODE -ne 0) { throw 'mcpb pack failed.' }

    Write-Host "$bundle ($([math]::Round((Get-Item $bundle).Length / 1MB, 1)) MB)"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
