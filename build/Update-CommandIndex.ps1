#Requires -Version 7.4
<#
.SYNOPSIS
Regenerates data/pnp-index.json, the search corpus, from the installed PnP.PowerShell module. Run before a release.

.DESCRIPTION
Unlike Update-VendoredData.ps1 this reads the module on this machine rather than GitHub, so
PnP.PowerShell must be installed. The corpus it emits feeds BM25 search and parameter validation.

Parameter sets reference parameters by index, which keeps the file roughly a third of the size
that repeating every name would cost.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..' 'data'),
    [string] $DocsUrlTemplate = 'https://pnp.github.io/powershell/cmdlets/{name}.html',
    [int] $MaxDescriptionChars = 400
)

$ErrorActionPreference = 'Stop'

$module = Get-Module -ListAvailable PnP.PowerShell | Sort-Object Version -Descending | Select-Object -First 1
if (-not $module) {
    throw "PnP.PowerShell is not installed. Run 'Install-Module PnP.PowerShell -Scope CurrentUser' first."
}

Import-Module PnP.PowerShell -ErrorAction Stop
Write-Host "Indexing PnP.PowerShell $($module.Version) from $($module.ModuleBase)"

$common = [System.Management.Automation.Cmdlet]::CommonParameters + [System.Management.Automation.Cmdlet]::OptionalCommonParameters

# PnP writes its synopsis as a permissions block followed by the real sentence. Split rather than index both.
function Split-Synopsis([string] $Text) {
    $lines = @($Text -split "`r?`n" | Where-Object { $_.Trim() })
    $permissions = @($lines | Where-Object { $_ -match '^\s*\*\s+\S' } | ForEach-Object { ($_ -replace '^\s*\*\s+', '').Trim() })
    $synopsis = @($lines | Where-Object { $_ -notmatch '^\s*\*' }) -join ' '

    # Some synopses carry markup -- Add-PnPListItem embeds an <a><img> batching badge -- which would be
    # indexed as words and shown to the user.
    $synopsis = $synopsis -replace '<[^>]+>', ' '
    $synopsis = ($synopsis -replace '\s+', ' ').Trim()

    [pscustomobject]@{ Synopsis = $synopsis; Permissions = $permissions }
}

# The MAML ships example code in maml:introduction and leaves dev:code empty, so read both.
function Get-ExampleLine($Example) {
    $text = @($Example.introduction | ForEach-Object { $_.Text }) -join "`n"
    if (-not ($text -match '\S')) { $text = [string]$Example.code }

    @($text -split "`r?`n" | Where-Object { $_.Trim() -and $_ -notmatch '^\s*```' }) | Select-Object -First 1
}

# Aliases carry no verb or noun and name superseded cmdlets (AzureAD -> EntraID), so they are
# recorded for resolution but kept out of the corpus rather than offered as search results.
$aliases = [ordered]@{}
foreach ($alias in Get-Command -Module PnP.PowerShell -CommandType Alias | Sort-Object Name) {
    $target = if ($alias.ResolvedCommand) { [string]$alias.ResolvedCommand.Name } else { [string]$alias.Definition }
    if ($target) { $aliases[$alias.Name] = $target }
}

$indexed = foreach ($command in Get-Command -Module PnP.PowerShell -CommandType Cmdlet, Function | Sort-Object Name) {
    $help = Get-Help $command.Name -ErrorAction SilentlyContinue
    $split = Split-Synopsis ([string]$help.Synopsis)

    # GetEnumerator, not .Keys: a cmdlet with a parameter named Keys (Set-PnPIndexedProperties) has it
    # shadow the dictionary's own property, which silently yields metadata objects instead of names.
    $parameters = @($command.Parameters.GetEnumerator() | ForEach-Object { $_.Key } | Where-Object { $_ -notin $common } | Sort-Object)
    $position = @{}
    for ($i = 0; $i -lt $parameters.Count; $i++) { $position[$parameters[$i]] = $i }

    $sets = @(foreach ($set in $command.ParameterSets) {
        $members = @($set.Parameters | Where-Object { $position.ContainsKey($_.Name) })

        [ordered]@{
            n = $set.Name
            d = [bool]$set.IsDefault
            i = @($members | ForEach-Object { $position[$_.Name] })
            r = @($members | Where-Object { $_.IsMandatory } | ForEach-Object { $position[$_.Name] })
        }
    })

    # A single unnamed set carries no information the parameter list does not already have.
    if ($sets.Count -eq 1 -and $sets[0].n -eq '__AllParameterSets' -and $sets[0].r.Count -eq 0) {
        $sets = @()
    }

    # Capped, and indexed at a lower weight than the synopsis: many synopses are too thin to match a
    # real question ("Add-PnPField" says only "Add a field"), while the description says "a column".
    $description = (@($help.Description | ForEach-Object { $_.Text }) -join ' ') -replace '<[^>]+>', ' '
    $description = ($description -replace '\s+', ' ').Trim()
    if ($description.Length -gt $MaxDescriptionChars) {
        $description = $description.Substring(0, $MaxDescriptionChars)
    }

    $entry = [ordered]@{
        n = $command.Name
        v = [string]$command.Verb
        u = [string]$command.Noun
        s = $split.Synopsis
        p = @(foreach ($name in $parameters) {
            [ordered]@{ n = $name; t = [string]$command.Parameters[$name].ParameterType.Name }
        })
    }

    if ($description) { $entry.d = $description }
    if ($split.Permissions.Count) { $entry.perms = $split.Permissions }
    if ($sets.Count) { $entry.ps = $sets }

    $examples = @($help.Examples.example | ForEach-Object { Get-ExampleLine $_ } | Where-Object { $_ } | Select-Object -First 2)
    if ($examples.Count) { $entry.e = $examples }

    $helpUri = [string]$command.HelpUri
    if ($helpUri -and $helpUri -ne $DocsUrlTemplate.Replace('{name}', $command.Name)) { $entry.h = $helpUri }

    $entry
}

# Depth 8 against a structure that nests 6 deep. Raising it further costs minutes of serialization
# for no extra data; VendoredIndexTests asserts nothing was dropped.
$json = [ordered]@{
    source          = 'Get-Command and Get-Help against the installed PnP.PowerShell module'
    moduleVersion   = [string]$module.Version
    docsUrlTemplate = $DocsUrlTemplate
    aliases         = $aliases
    commands        = @($indexed)
} | ConvertTo-Json -Depth 8 -Compress

$path = Join-Path $OutputDirectory 'pnp-index.json'
$json | Set-Content $path -Encoding utf8NoBOM

$withSynopsis = @($indexed | Where-Object { $_.s }).Count
$withExamples = @($indexed | Where-Object { $_.e }).Count
$parameterCount = ($indexed | ForEach-Object { $_.p.Count } | Measure-Object -Sum).Sum

Write-Host "pnp-index.json: $($indexed.Count) cmdlets, $($aliases.Count) aliases, $parameterCount parameters, $withSynopsis with a synopsis, $withExamples with examples"
Write-Host ("                {0:n0} KB at module version {1}" -f ($json.Length / 1KB), $module.Version)
