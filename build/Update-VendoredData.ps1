#Requires -Version 7.4
<#
.SYNOPSIS
Regenerates the vendored indexes in data/ from pnp/vscode-pnp-powershell. Run before a release.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..' 'data')
)

$ErrorActionPreference = 'Stop'

$repo = 'pnp/vscode-pnp-powershell'
$sampleUrlTemplate    = 'https://pnp.github.io/script-samples/{name}/README.html'
$sampleRawUrlTemplate = 'https://raw.githubusercontent.com/pnp/script-samples/main/scripts/{name}/README.md'
$markdownUrlTemplate  = 'https://raw.githubusercontent.com/pnp/powershell/dev/documentation/{name}.md'
$docsUrlTemplate      = 'https://pnp.github.io/powershell/cmdlets/{name}.html'

function Get-SourceCommit([string] $Path) {
    $headers = @{ 'User-Agent' = 'pnp-powershell-mcp-server' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
    (Invoke-RestMethod "https://api.github.com/repos/$repo/commits?path=$Path&per_page=1" -Headers $headers)[0].sha
}

function Get-SourceJson([string] $Path) {
    Invoke-RestMethod "https://raw.githubusercontent.com/$repo/main/$Path"
}

function Expand-Template([string] $Template, [string] $Name) {
    $Template.Replace('{name}', $Name)
}

$generated = (Get-Date).ToString('yyyy-MM-dd')

# Script samples

$samplesCommit = Get-SourceCommit 'data/samples.json'
$samples = (Get-SourceJson 'data/samples.json').samples

$indexed = foreach ($sample in $samples) {
    if ($sample.rawUrl -notmatch '/scripts/([^/]+)/README\.md$') {
        throw "Sample '$($sample.title)' has an unexpected rawUrl '$($sample.rawUrl)'. Update the templates in this script."
    }
    $name = $Matches[1]

    if ($sample.url -ne (Expand-Template $sampleUrlTemplate $name)) {
        throw "Sample '$name' has an off-template url '$($sample.url)'. Update the templates in this script."
    }

    [ordered]@{
        name        = $name
        title       = $sample.title
        description = $sample.description
        tags        = @($sample.tags | Where-Object { $_ })
        authors     = @($sample.authors | Where-Object { $_.name } | ForEach-Object { @{ name = $_.name } })
    }
}

[ordered]@{
    source         = "https://github.com/$repo/blob/main/data/samples.json"
    commit         = $samplesCommit
    generated      = $generated
    urlTemplate    = $sampleUrlTemplate
    rawUrlTemplate = $sampleRawUrlTemplate
    samples        = @($indexed)
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'script-samples.json') -Encoding utf8NoBOM

Write-Host "script-samples.json: $($indexed.Count) samples at $($samplesCommit.Substring(0,7))"

# Cmdlet index

$commandsCommit = Get-SourceCommit 'data/pnpPsModel.json'
$commands = (Get-SourceJson 'data/pnpPsModel.json').commands

foreach ($command in $commands) {
    if ($command.url -ne (Expand-Template $markdownUrlTemplate $command.name) -or
        $command.docs -ne (Expand-Template $docsUrlTemplate $command.name)) {
        throw "Cmdlet '$($command.name)' has off-template documentation URLs. Update the templates in this script."
    }
}

[ordered]@{
    source              = "https://github.com/$repo/blob/main/data/pnpPsModel.json"
    commit              = $commandsCommit
    generated           = $generated
    markdownUrlTemplate = $markdownUrlTemplate
    docsUrlTemplate     = $docsUrlTemplate
    commands            = @($commands.name | Sort-Object)
} | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutputDirectory 'pnp-commands.json') -Encoding utf8NoBOM

Write-Host "pnp-commands.json: $($commands.Count) cmdlets at $($commandsCommit.Substring(0,7))"
