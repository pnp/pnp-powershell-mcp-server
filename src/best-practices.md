# PnP.PowerShell MCP Server — Best Practices

## Recommended Tool Workflow

Always follow this sequence when automating Microsoft 365 tasks:

1. **`pnpSearchCmdlets`** — Discover the relevant cmdlets for the task.
2. **`pnpGetCmdletDocs`** — Fetch full documentation for each candidate cmdlet.
   Review parameter names, types, required vs. optional, and the examples section.
3. **`pnpRunCmdlet`** — Execute the expression only after confirming correct syntax from docs.

Skipping step 2 is the most common cause of parameter name errors and unexpected results.

---

## Prerequisites

Ensure the following are present before invoking any cmdlet:

1. **PowerShell 7+** (`pwsh`) — available in PATH.
   - Windows: `winget install Microsoft.PowerShell`
   - macOS: `brew install --cask powershell`
   - Linux: https://docs.microsoft.com/powershell/scripting/install/installing-powershell-on-linux

2. **PnP.PowerShell module** — installed for PowerShell 7+:
   ```powershell
   Install-Module PnP.PowerShell -Scope CurrentUser
   ```

3. **A registered Entra ID application** with the permissions required for your scripts.
   See: https://pnp.github.io/powershell/articles/registerapplication.html

---

## Authentication

The MCP server does **not** manage authentication. You must call `Connect-PnPOnline`
yourself in a PowerShell session before invoking authenticated cmdlets via `pnpRunCmdlet`.

`Connect-PnPOnline` stores a token cache to disk. Each `pnpRunCmdlet` call spawns a
**fresh `pwsh` process** that reads this cache automatically, so a single prior
connection is sufficient for the duration of the token's validity.

### Interactive / Device Code (development and testing)

```powershell
Connect-PnPOnline -Url "https://<tenant>.sharepoint.com" -Interactive
# or
Connect-PnPOnline -Url "https://<tenant>.sharepoint.com" -DeviceLogin
```

### Certificate-based (recommended for CI/CD and automation)

```powershell
Connect-PnPOnline -Url "https://<tenant>.sharepoint.com" `
  -ClientId "<app-id>" `
  -Tenant "<tenant-id>" `
  -CertificatePath "./cert.pfx" `
  -CertificatePassword (ConvertTo-SecureString -String "<password>" -AsPlainText -Force)
```

### Managed Identity (Azure-hosted workloads)

```powershell
Connect-PnPOnline -Url "https://<tenant>.sharepoint.com" -ManagedIdentity
```

### Check connection state before running cmdlets

```powershell
$conn = Get-PnPConnection
if ($null -eq $conn) {
    Write-Error "Not connected. Call Connect-PnPOnline first."
}
```

If `pnpRunCmdlet` returns an authentication or access-denied error, run the
connection check above in your own PowerShell session and reconnect.

---

## Stateless Execution Model

Each `pnpRunCmdlet` call spawns a **new, independent `pwsh` process**. This has
important implications:

- **Variables do not persist between calls.** A variable declared in one call is
  not available in the next. Build self-contained, single-expression scripts.
- **The PnP.PowerShell module is imported automatically** at the start of every
  execution. You do not need to include `Import-Module` in your expression.
- **`$ErrorActionPreference` is set to `"Stop"`** automatically. All errors are
  terminating and will be caught and returned as `[ERROR]` prefixed strings.
- **`-Verbose`, `-Debug`, `-Warning`, and `-Information` streams are silenced**
  automatically. Do not rely on these streams for output.

### Multi-step operations

When a task requires multiple steps that depend on each other, combine them into a
single expression using semicolons or a script block:

```powershell
$list = New-PnPList -Title "Project Tasks" -Template GenericList; `
Add-PnPField -List $list -DisplayName "Status" -InternalName "Status" -Type Choice -Choices "Not Started","In Progress","Done"; `
Add-PnPField -List $list -DisplayName "Owner" -InternalName "Owner" -Type User; `
Write-Output "List created: $($list.Title)"
```

---

## Error Handling

When `pnpRunCmdlet` returns an `[ERROR]` prefixed string:

```
[ERROR] PowerShell exited with code 1.
Error ID: AccessDeniedException
Details: Access denied. You do not have permission to perform this action.
```

Recovery steps:
1. Read the error message carefully — it typically identifies the exact cause.
2. Check `Error ID` for the `FullyQualifiedErrorId` — this is the most precise
   signal for identifying the problem category.
3. Verify that `Connect-PnPOnline` was called and the token is still valid.
4. Confirm your Entra ID app has the required permissions for the operation.
5. Run `pnpGetCmdletDocs` to re-check parameter names and required values.
6. Simplify the expression to the minimum and retry to isolate the failing part.
7. For timeout errors (`Command timed out after 120s`), break the operation into
   smaller batches or target a more specific scope.

### Common error patterns

| Error ID | Likely cause | Action |
|---|---|---|
| `AccessDeniedException` | Missing permission or no active connection | Reconnect; check app permissions |
| `InvalidOperation` | Wrong parameter combination or invalid state | Review docs for correct syntax |
| `ItemNotFoundException` | Target object does not exist | Verify site URL, list name, or ID |
| `ArgumentException` | Invalid parameter value | Check parameter type and allowed values in docs |

---

## Output Formatting

Each spawned `pwsh` process returns plain text. Use explicit formatters so the
output is structured and machine-readable:

- **`ConvertTo-Json`** — preferred for programmatic use:
  ```powershell
  Get-PnPList | ConvertTo-Json -Depth 5
  ```
- **`Select-Object`** — reduce output to relevant properties before serialising:
  ```powershell
  Get-PnPList | Select-Object Title, Id, ItemCount | ConvertTo-Json
  ```
- **`Format-Table`** — human-readable summary (not suitable for parsing):
  ```powershell
  Get-PnPSite | Format-Table Title, Url -AutoSize
  ```
- **`-Depth`** on `ConvertTo-Json` — increase when objects have nested properties
  that are truncated (default depth is 2):
  ```powershell
  Get-PnPField -List "Tasks" | Select-Object Title, InternalName, TypeAsString | ConvertTo-Json -Depth 3
  ```

Avoid `-Verbose`, `-Debug`, or `-Information` in expressions — these streams are
suppressed and will produce no output.

---

## Security Guidance

- **Never embed credentials** (passwords, secrets, tokens) in expressions passed
  to `pnpRunCmdlet`. Use certificate-based or managed identity authentication.
- **Prefer least privilege** — register an Entra ID app with only the permissions
  your scripts actually require.
- **Validate all dynamic inputs** before composing expressions. If a value comes
  from user input, sanitise it to prevent injection into the PowerShell command.
- **Token cache location** — the PnP token cache is stored in the current user's
  profile directory. On shared machines, ensure the profile is access-controlled.
- **Avoid destructive operations without prior verification** — cmdlets such as
  `Remove-PnPList`, `Remove-PnPSite`, `Remove-PnPTeamsTeam`, and similar are
  irreversible. Always retrieve and confirm the target before executing.
- **Test in a non-production environment first** when automating bulk operations
  or applying configuration changes to sites and groups.

---

## Common Patterns

### List all SharePoint sites in the tenant

```powershell
Get-PnPTenantSite | Select-Object Title, Url, Template, StorageUsageCurrent | ConvertTo-Json
```

### Get all lists in a site

```powershell
Get-PnPList | Select-Object Title, Id, ItemCount, BaseType | ConvertTo-Json
```

### Create a SharePoint list with custom fields

```powershell
$list = New-PnPList -Title "My List" -Template GenericList; `
Add-PnPField -List $list -DisplayName "Description" -InternalName "Description" -Type Note; `
Add-PnPField -List $list -DisplayName "Priority" -InternalName "Priority" -Type Choice -Choices "High","Medium","Low"; `
Write-Output "Created list: $($list.Title) with Id: $($list.Id)"
```

### Add items to a list

```powershell
Add-PnPListItem -List "My List" -Values @{ Title = "Item 1"; Priority = "High" } | Select-Object Id | ConvertTo-Json
```

### Query list items with a filter

```powershell
Get-PnPListItem -List "My List" -Query "<View><Query><Where><Eq><FieldRef Name='Priority'/><Value Type='Choice'>High</Value></Eq></Where></Query></View>" | ForEach-Object { $_.FieldValues } | ConvertTo-Json -Depth 3
```

### Create a Teams team

```powershell
New-PnPTeamsTeam -DisplayName "My Team" -Description "Team description" -Visibility Private | Select-Object DisplayName, GroupId | ConvertTo-Json
```

### Get all members of a Microsoft 365 group

```powershell
Get-PnPMicrosoft365GroupMember -Identity "MyGroup" | Select-Object DisplayName, UserPrincipalName, UserType | ConvertTo-Json
```

### Get site permissions

```powershell
Get-PnPSiteCollectionAdmin | Select-Object Title, LoginName | ConvertTo-Json
```

### Apply a site design

```powershell
Invoke-PnPSiteDesign -Identity "<site-design-id>" -WebUrl "https://<tenant>.sharepoint.com/sites/<site>"
```

### Batch add items with error handling

```powershell
$items = @("Item A","Item B","Item C"); `
$results = $items | ForEach-Object { `
  Add-PnPListItem -List "My List" -Values @{ Title = $_ } | Select-Object Id `
}; `
$results | ConvertTo-Json
```

---

## Timeout Considerations

`pnpRunCmdlet` enforces a **120-second hard timeout**. Operations that may exceed
this limit include:

- Tenant-wide queries across many sites
- Bulk item operations on large lists (thousands of items)
- Site collection provisioning

To stay within the timeout:
- Target a specific site URL rather than querying all sites.
- Use `-PageSize` or `-RowLimit` parameters to page through large result sets.
- Break bulk operations into batches across multiple `pnpRunCmdlet` calls.
- Use `-Includes` or `Select-Object` to retrieve only the properties you need,
  reducing serialisation time.
