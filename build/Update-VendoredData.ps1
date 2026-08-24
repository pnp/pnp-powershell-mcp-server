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
    $commit = (Invoke-RestMethod "https://api.github.com/repos/$repo/commits?path=$Path&per_page=1" -Headers $headers)[0]

    # Dated from the commit, not from today, so regenerating unchanged upstream produces no diff.
    [pscustomobject]@{ Sha = $commit.sha; Date = ([datetime]$commit.commit.committer.date).ToString('yyyy-MM-dd') }
}

function Get-SourceJson([string] $Path) {
    Invoke-RestMethod "https://raw.githubusercontent.com/$repo/main/$Path"
}

function Expand-Template([string] $Template, [string] $Name) {
    $Template.Replace('{name}', $Name)
}

# Script samples

$samples_ = Get-SourceCommit 'data/samples.json'
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
    commit         = $samples_.Sha
    sourceDate     = $samples_.Date
    urlTemplate    = $sampleUrlTemplate
    rawUrlTemplate = $sampleRawUrlTemplate
    samples        = @($indexed)
} | ConvertTo-Json -Depth 6 -Compress | Set-Content (Join-Path $OutputDirectory 'script-samples.json') -Encoding utf8NoBOM

Write-Host "script-samples.json: $($indexed.Count) samples at $($samples_.Sha.Substring(0,7))"

# Cmdlet index

$commands_ = Get-SourceCommit 'data/pnpPsModel.json'
$commands = (Get-SourceJson 'data/pnpPsModel.json').commands

foreach ($command in $commands) {
    if ($command.url -ne (Expand-Template $markdownUrlTemplate $command.name) -or
        $command.docs -ne (Expand-Template $docsUrlTemplate $command.name)) {
        throw "Cmdlet '$($command.name)' has off-template documentation URLs. Update the templates in this script."
    }
}

[ordered]@{
    source              = "https://github.com/$repo/blob/main/data/pnpPsModel.json"
    commit              = $commands_.Sha
    sourceDate          = $commands_.Date
    markdownUrlTemplate = $markdownUrlTemplate
    docsUrlTemplate     = $docsUrlTemplate
    commands            = @($commands.name | Sort-Object)
} | ConvertTo-Json -Depth 3 -Compress | Set-Content (Join-Path $OutputDirectory 'pnp-commands.json') -Encoding utf8NoBOM

Write-Host "pnp-commands.json: $($commands.Count) cmdlets at $($commands_.Sha.Substring(0,7))"
