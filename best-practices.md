# PnP.PowerShell MCP Server — Best Practices

## Prerequisites

Before using the MCP server tools, ensure the following are installed:

1. **PowerShell 7+** (`pwsh`) — available in PATH.
   - Windows: `winget install Microsoft.PowerShell`
   - macOS: `brew install --cask powershell`
   - Linux: https://docs.microsoft.com/powershell/scripting/install/installing-powershell-on-linux

2. **PnP.PowerShell module** — installed in PowerShell 7+:
   ```powershell
   Install-Module PnP.PowerShell -Scope CurrentUser
   ```

3. **A registered Entra ID application** with the permissions required for your scripts.
   See: https://pnp.github.io/powershell/articles/registerapplication.html

---

## Authentication

The MCP server does **not** manage authentication. You must call `Connect-PnPOnline`
yourself in a PowerShell session before invoking cmdlets through `pnpRunCmdlet`.

`Connect-PnPOnline` stores a token cache to disk. Each `pnpRunCmdlet` invocation
spawns a fresh `pwsh` process that reads this cache automatically, so you only need
to connect once per session (or when the token expires).

### Interactive / Device Code (development & testing)

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

---

## Error Handling

The `pnpRunCmdlet` tool sets `$ErrorActionPreference = "Stop"` automatically.
This means any terminating error (including non-terminating ones promoted by
`-ErrorAction Stop`) is caught and returned as an `[ERROR]` prefixed string.

When you receive an error:
1. Read the error message carefully — it often tells you the exact problem.
2. Check that `Connect-PnPOnline` was called and the connection is active.
3. Verify you have the required permissions for the operation.
4. Use `pnpGetCmdletDocs` to review the correct parameter syntax.
5. Simplify the expression and retry with fewer parameters to isolate the issue.

**Example error response:**
```
[ERROR] PowerShell exited with code 1.
Error ID: AccessDeniedException
Details: Access denied. You do not have permission to perform this action.
```

---

## Output Formatting

By default, cmdlets return PowerShell objects that are serialised to text.
For structured output, use one of:

- **`ConvertTo-Json`** — convert output to JSON for easy parsing:
  ```powershell
  Get-PnPList | ConvertTo-Json -Depth 5
  ```
- **`Select-Object`** — limit output to specific properties:
  ```powershell
  Get-PnPList | Select-Object Title, Id, ItemCount | ConvertTo-Json
  ```
- **`Format-Table`** — human-readable table (for display, not parsing):
  ```powershell
  Get-PnPSite | Format-Table Title, Url -AutoSize
  ```

Avoid using `-Verbose`, `-Debug`, or `-Information` flags in automated expressions
as they add noise to the output stream.

---

## Security Guidance

- **Never embed credentials** (passwords, secrets, tokens) directly in expressions
  passed to `pnpRunCmdlet`. Use certificate-based or managed identity auth instead.
- **Prefer least-privilege** — register an Entra ID app with only the permissions
  your scripts actually need.
- **Validate inputs** — if building expressions dynamically, validate and sanitise
  any user-provided values before including them in a `pnpRunCmdlet` call.
- **Token cache location** — the PnP token cache is stored in the user's profile.
  On shared machines, ensure the profile is appropriately access-controlled.
- **Avoid destructive operations without confirmation** — cmdlets such as
  `Remove-PnPList`, `Remove-PnPSite`, etc. are irreversible. Always verify
  the target before executing.

---

## Common Patterns

### List all SharePoint sites

```powershell
Get-PnPTenantSite | Select-Object Title, Url, Template | ConvertTo-Json
```

### Create a new SharePoint list with columns

```powershell
$list = New-PnPList -Title "My List" -Template GenericList
Add-PnPField -List $list -DisplayName "Description" -InternalName "Description" -Type Text
```

### Add items to a list

```powershell
Add-PnPListItem -List "My List" -Values @{ Title = "Item 1"; Description = "Hello" }
```

### Create a Teams team

```powershell
New-PnPTeamsTeam -DisplayName "My Team" -Description "Team description" -Visibility Private
```

### Get all members of a Microsoft 365 group

```powershell
Get-PnPMicrosoft365GroupMember -Identity "MyGroup" | Select-Object DisplayName, UserPrincipalName | ConvertTo-Json
```
