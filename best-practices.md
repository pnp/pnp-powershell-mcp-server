# Best Practices for Using PnP PowerShell via MCP Server

This guide provides best practices for using PnP PowerShell commands through the MCP server, including authentication, error handling, and execution tips.

## Recommended Workflow

Use this flow for reliable execution:

1. **Check connection** with `pnp_get_connection_status` to see if you are already authenticated.
2. **Search commands** with `pnp_search_commands` to find the right command for your task.
3. **Read documentation** with `pnp_get_command_docs` to understand syntax, parameters, and examples.
4. **Search community samples** with `pnp_search_script_samples` or `pnp_suggest_script` before writing a script from scratch — there is a good chance someone has already solved a similar problem.
5. **Execute commands** with `pnp_run_command` in small, verifiable steps.

## Prerequisites

- **PowerShell 7+** (`pwsh`) must be installed and available on `PATH`. Every tool in this server shells out to `pwsh` to run PnP PowerShell — if it's missing, tools will return an actionable error telling you to install it.
- **The `PnP.PowerShell` module** must be installed:
  ```powershell
  Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force
  ```
  Every tool checks for the module before running and returns a clear error with the install command above if it's missing, instead of a raw PowerShell exception.

## Authentication Best Practices

### Connect to SharePoint Online

Before running any PnP PowerShell commands, establish a connection:

```powershell
# Interactive login (recommended for local/manual use, supports MFA)
Connect-PnPOnline -Url https://contoso.sharepoint.com/sites/MySite -Interactive

# Certificate-based authentication (recommended for automation/CI-CD)
Connect-PnPOnline -Url https://contoso.sharepoint.com -ClientId <app-id> -Tenant contoso.onmicrosoft.com -Thumbprint <cert-thumbprint>

# Managed Identity (recommended for Azure-hosted scenarios)
Connect-PnPOnline -Url https://contoso.sharepoint.com -ManagedIdentity
```

### Authentication Methods

- **Interactive scenarios**: Use `-Interactive` for browser-based authentication with MFA support.
- **Automation/CI-CD**: Use certificate-based authentication (`-ClientId`, `-Tenant`, `-Thumbprint`) or managed identity (`-ManagedIdentity`).
- **Avoid** storing credentials directly in scripts. Use Azure Key Vault or environment variables.
- **Check connection** with `pnp_get_connection_status` before running commands to avoid authentication errors.

## Execution Best Practices

### General Tips

- **Prefer reads before writes**: Run `Get-*` commands before `Set-*`, `Add-*`, or `Remove-*` to verify state.
- **Break complex tasks into steps**: Run commands incrementally via `pnp_run_command` and validate outputs between steps rather than chaining an entire script blindly.
- **Limit output size**: Use `Select-Object` to return only the properties you need — this keeps responses concise and token-efficient.
- **Assign before shaping**: Store a `Get-*` result in a variable before piping it into `Select-Object` (see below) — piping directly can silently return empty data.
- **Be explicit**: Use full site URLs, tenant identifiers, and object IDs to reduce ambiguity.
- **Use error handling**: Wrap command chains in `try/catch` blocks.

### Assign Results to a Variable Before Shaping Them

Some PnP cmdlets lose their property values when piped **directly** into `Select-Object`. The pipeline returns a single object with every property `null` instead of the real results — and it does so **silently**, with no error, so the wrong answer looks like a valid one:

```powershell
# ❌ Returns 1 object, every property null
Get-PnPTeamsTeam | Select-Object DisplayName, Visibility, GroupId

# ✅ Returns all teams, fully populated
$teams = Get-PnPTeamsTeam
$teams | Select-Object DisplayName, Visibility, GroupId
```

Verified against `Get-PnPTeamsTeam` on a tenant with 30 teams: the first form yields 1 null object, the second yields all 30. The same shape applies to `ConvertTo-Json`, `Where-Object`, and `Sort-Object`.

Because the failure is silent, treat this as the default habit:

1. Assign the `Get-*` result to a variable.
2. Check `@($result).Count` before trusting anything derived from it.
3. Then project, filter, sort, or convert from the variable.

If a query returns suspiciously few results — especially exactly one row of nulls — re-run it via a variable before concluding the tenant has no data.

### Output Management

```powershell
# Limit properties returned — assign first, then shape
$lists = Get-PnPList
$lists | Select-Object Title, ItemCount, LastItemModifiedDate

# Filter results
Get-PnPListItem -List "Documents" | Where-Object { $_.FieldValues.Author -like '*John*' }

# Page large result sets
Get-PnPListItem -List "LargeList" -PageSize 500
```

### Error Handling in Command Chains

```powershell
try {
    $web = Get-PnPWeb -ErrorAction Stop
    Write-Output "Connected to: $($web.Title)"
}
catch {
    Write-Output "Error: $($_.Exception.Message)"
}
```

## Common Patterns

### Site Management

```powershell
# List all site collections
$sites = Get-PnPTenantSite
$sites | Select-Object Url, Title, Template, StorageUsage

# Create a new site
New-PnPSite -Type CommunicationSite -Title "Project Hub" -Url https://contoso.sharepoint.com/sites/ProjectHub

# Get site details
Get-PnPSite -Includes Owner, Usage, StorageQuota
```

### List & Library Operations

```powershell
# Get all lists
$lists = Get-PnPList
$lists | Select-Object Title, ItemCount, BaseTemplate

# Get list items with specific fields
Get-PnPListItem -List "Tasks" -Fields "Title", "Status", "AssignedTo" -PageSize 100

# Add a list item
Add-PnPListItem -List "Tasks" -Values @{"Title"="New Task"; "Status"="Not Started"}
```

### User & Permission Management

```powershell
# Get site users
$users = Get-PnPUser
$users | Select-Object Title, Email, LoginName

# Add user to group
Add-PnPGroupMember -LoginName "user@contoso.com" -Group "Site Members"

# Check permissions
Get-PnPSiteCollectionAdmin
```

### Microsoft Teams

```powershell
# Get all teams — assign first; piping Get-PnPTeamsTeam straight into
# Select-Object returns a single all-null object (see "Assign Results to a
# Variable Before Shaping Them" above)
$teams = Get-PnPTeamsTeam
$teams | Select-Object DisplayName, GroupId, Visibility

# Get team channels
Get-PnPTeamsChannel -Team "Marketing Team"
```

### Entra ID (Azure AD)

```powershell
# Get Entra ID users
$aadUsers = Get-PnPAzureADUser
$aadUsers | Select-Object DisplayName, UserPrincipalName, AccountEnabled

# Get Entra ID groups
$aadGroups = Get-PnPAzureADGroup
$aadGroups | Select-Object DisplayName, GroupTypes, SecurityEnabled
```

## Working with Complex Data

When you need to pass complex JSON or object data to a cmdlet, prefer building it as a PowerShell hashtable in the same command rather than trying to inline large JSON strings — it's less error-prone through the base64-encoded execution path used by `pnp_run_command`:

```powershell
$values = @{ Title = "New Task"; Status = "Not Started"; Priority = "High" }
Add-PnPListItem -List "Tasks" -Values $values
```

## Debugging and Verbose Output

```powershell
# Verbose mode
Get-PnPWeb -Verbose

# Detailed error information
Get-PnPWeb -ErrorAction Stop -ErrorVariable err; $err
```

## Areas Covered by PnP PowerShell

PnP PowerShell can manage many Microsoft 365 areas including:

- **SharePoint Online**: Sites, lists, libraries, pages, web parts, content types, site designs
- **Microsoft Teams**: Teams, channels, tabs, apps
- **Entra ID (Azure AD)**: Users, groups, app registrations, service principals
- **OneDrive**: Files, sharing, storage
- **Planner**: Plans, tasks, buckets
- **Power Platform**: Power Apps, Power Automate flows
- **Microsoft 365 Groups**: Group management, membership
- **Taxonomy**: Term store, term groups, term sets
- **Search**: Search configuration, result sources, query rules
- **Tenant Administration**: Site creation, storage quotas, sharing settings

## Summary

1. **Check `pnp_get_connection_status`** before assuming you're authenticated.
2. **Search first** with `pnp_search_commands` and `pnp_search_script_samples` instead of guessing cmdlet names.
3. **Read the docs** with `pnp_get_command_docs` for any unfamiliar cmdlet.
4. **Execute incrementally** with `pnp_run_command` — validate each step's output before moving to the next.
5. **Use certificate or managed-identity auth** for unattended/automation scenarios.
6. **Keep output concise** with `Select-Object` and `-PageSize` to reduce token usage.
7. **Assign `Get-*` results to a variable** before piping into `Select-Object` — piping directly can silently return a single all-null object instead of your data.
8. **Never hardcode credentials** in scripts.

For more information, refer to the [PnP PowerShell documentation](https://pnp.github.io/powershell/).
