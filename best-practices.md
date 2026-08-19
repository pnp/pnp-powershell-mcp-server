# Best Practices for Using PnP PowerShell via MCP Server

This guide provides best practices for using PnP PowerShell commands through the MCP server, including authentication, error handling, and execution tips.

## Recommended Workflow

Use this flow for reliable execution:

1. **Check connection** with `pnp_get_connection_status` to see if you are already authenticated.
2. **Search commands** with `pnp_search_commands` to find the right command for your task.
3. **Read documentation** with `pnp_get_command_docs` to understand syntax, parameters, and examples. Both this and `pnp_search_commands` return the cmdlet's published documentation URL, which is worth citing to the user and often carries examples the shipped help omits.
4. **Search community samples** with `pnp_search_script_samples` or `pnp_suggest_script` before writing a script from scratch — there is a good chance someone has already solved a similar problem.
5. **Execute commands** with `pnp_run_command` in small, verifiable steps.

This guide is long, so `pnp_get_best_practices` accepts an optional `section` — `workflow`, `docs`,
`sessions`, `config`, `readonly`, `output`, `destructive`, `auth`, `execution` or `patterns` — to return one
topic instead of everything. Pull `readonly` when a command is refused, `destructive` before a
confirmation prompt, or `patterns` when looking for a worked example.

## Finding More About a Cmdlet

Every cmdlet carries a `HelpUri` — its page on <https://pnp.github.io/powershell/>. Both
`pnp_search_commands` (as a `HelpUri` field per result) and `pnp_get_command_docs` (as an
`ONLINE DOCUMENTATION:` line) return it.

The local help returned by `pnp_get_command_docs` is generated from the installed module, so it can
lag the published page and sometimes omits examples entirely. Reach for the URL when the local help is
not enough.

### Worked example

You need to filter list items server-side and `Get-PnPListItem`'s local help does not explain `-Query`:

1. Find the cmdlet:

   ```jsonc
   // pnp_search_commands
   { "query": "list item" }
   ```

   ```jsonc
   { "Name": "Get-PnPListItem", "Verb": "Get", "Noun": "PnPListItem",
     "HelpUri": "https://pnp.github.io/powershell/cmdlets/Get-PnPListItem.html" }
   ```

2. Read the local help:

   ```jsonc
   // pnp_get_command_docs
   { "commandName": "Get-PnPListItem" }
   ```

   The output ends with:

   ```text
   ONLINE DOCUMENTATION: https://pnp.github.io/powershell/cmdlets/Get-PnPListItem.html
   ```

3. If the parameter is still unclear, **fetch that URL with whatever web-fetch tool the client
   provides** and read the parameter and examples sections. If no fetch tool is available, give the
   user the link rather than guessing at the syntax.

4. Build the command from what the page shows, then run it in a small verifiable step.

### When there is no link

A cmdlet may report no `HelpUri` — `null` in search results, and no `ONLINE DOCUMENTATION:` line from
`pnp_get_command_docs`. That almost always means an **older `PnP.PowerShell` build**; current versions
populate it for effectively every cmdlet. `pnp_get_command_docs` says so and offers the fallback
directly in its output.

What to do instead, in order:

1. **Search the docs site** rather than guessing a URL: <https://pnp.github.io/powershell/> has a
   search box, and a web search for `PnP PowerShell <Cmdlet-Name>` normally lands on the right page.
2. **Suggest updating the module** if the user keeps hitting it:

   ```powershell
   Update-Module PnP.PowerShell
   ```

   Then restart the MCP server, since the module is imported once when a session starts.
3. **Fall back to the local help** (`pnp_get_command_docs`) and `Get-Command <Name> -Syntax` for the
   parameter list, and tell the user the online page could not be linked.

Never hand-assemble a documentation URL from the cmdlet name. The path pattern is not guaranteed, and a
fabricated link that 404s is worse than no link.

### Rules of thumb

- **Do not guess parameter names.** If the local help does not list it, fetch the page or ask. A
  guessed parameter fails with "A parameter cannot be found that matches parameter name".
- **Cite the link when you explain a cmdlet** to the user, so they can verify it themselves.
- **Use the returned `HelpUri` as-is** rather than constructing one; it is authoritative.
- The pages are public documentation, so fetching one needs no tenant connection and leaks nothing
  about the tenant.

## Prerequisites

- **PowerShell 7.4 or above** (`pwsh`) must be installed and available on `PATH`. This server runs PnP PowerShell in a `pwsh` session — if it's missing, tools will return an actionable error telling you to install it.
- **The `PnP.PowerShell` module** must be installed:
  ```powershell
  Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force
  ```
  The module is imported once when a session starts, and a clear error with the install command above is returned if it is missing, instead of a raw PowerShell exception.

## Sessions

Commands run inside a **persistent PowerShell session**, so state created by one call is still there
on the next one. In particular, a connection made with `Connect-PnPOnline` stays alive — connect
once, then keep running commands against it.

- **Reuse the connection.** Do not re-run `Connect-PnPOnline` before every command. Check with
  `pnp_get_connection_status` and connect only when it reports you are not connected.
- **`sessionId`.** Accepted by `pnp_run_command`, `pnp_get_connection_status` and `pnp_reset_session`.
  **Leave it unset for normal work** — everything then shares the session named `default`. Use a second
  name only to hold two tenant or account connections at the same time, because one session holds one
  connection:

  ```jsonc
  { "sessionId": "contoso",  "command": "Connect-PnPOnline -Url https://contoso.sharepoint.com -Interactive" }
  { "sessionId": "fabrikam", "command": "Connect-PnPOnline -Url https://fabrikam.sharepoint.com -Interactive" }
  { "sessionId": "contoso",  "command": "(Get-PnPTenantSite).Count" }
  ```

  Each session has its own connection **and** its own variables, so a `$sites` set in one is not
  visible in the other. `pnp_search_commands` and `pnp_get_command_docs` always use `default`, since
  cmdlet lookup does not depend on the connection.
- **One command at a time per session.** A second call against a busy session waits, then reports that
  the session is busy. Use a different `sessionId` to genuinely run two things at once.
- **Ending a session.** Use `pnp_reset_session` to sign out, switch accounts, or recover a session
  that has stopped responding. Everything in that session is discarded.
- **Idle sessions** are ended automatically after 30 minutes; just reconnect if that happens.
- **Timeouts.** A single command is capped at 10 minutes, after which the session is terminated and
  the connection is lost. Override with the `PNP_MCP_COMMAND_TIMEOUT_SECONDS` environment variable.
  Clients that support the MCP Tasks extension can run `pnp_run_command` as a task and poll it
  instead of holding the call open.

## Server Configuration

Five environment variables control behaviour. They are set by the user in their **MCP client config**
(see the [README](./README.md#configuration) for per-client examples) and take effect only after the
server restarts — this server cannot change them at runtime. If one is in the way, say which variable
to set rather than working around it.

| Variable | Default | Effect |
| --- | --- | --- |
| `PNP_MCP_READONLY` | `false` | `true` refuses anything that would change Microsoft 365. |
| `PNP_MCP_COMMAND_TIMEOUT_SECONDS` | `600` | Per-command wall-clock limit, in seconds. |
| `PNP_MCP_CONFIRM_DESTRUCTIVE` | `true` | `false` skips destructive confirmations. |
| `PNP_MCP_MAX_OUTPUT_CHARS` | `50000` | Largest tool response, in characters; longer output is truncated. |
| `PNP_SCRIPT_SAMPLES_PATH` | _(unset)_ | Local clone of the script samples repo, used when GitHub is unreachable. |

Both booleans are matched exactly: read-only turns on only for the literal `true`, and confirmation
turns off only for the literal `false`. `1` and `yes` leave the default in place.

## Output Size

Tool responses are capped (50,000 characters by default). When output is truncated you will see
`[output truncated: N of M characters omitted]`.

**Treat truncated output as incomplete.** It is not necessarily valid JSON, so do not parse it or
summarise it as though it were the whole result — a truncated list of sites is not "all the sites".
Instead, narrow the query and run it again:

```powershell
# Instead of everything
Get-PnPListItem -List "Documents"

# Page it, and return only the fields you need
Get-PnPListItem -List "Documents" -PageSize 500 | Select-Object Id, Title
```

Counting rather than listing often answers the question outright: `(Get-PnPTenantSite).Count`. If the
user genuinely needs the full set, tell them to raise `PNP_MCP_MAX_OUTPUT_CHARS`, or write the results
to a file with `Export-Csv` and report the path instead of the contents.

## Read-Only Mode

Set `PNP_MCP_READONLY=true` to refuse anything that would change Microsoft 365.

Classification is by **verb**, resolved by parsing the script with PowerShell's own parser — so an
alias is followed to its target (`rm` is treated as `Remove-Item`) rather than taken at face value.

### Allowed verbs

| Verb | Why |
| --- | --- |
| `Get-` | Reads |
| `Export-` | Reads; writes a local file |
| `Test-` | Checks without changing anything |
| `Convert-` / `ConvertTo-` / `ConvertFrom-` | Transforms values and local files |
| `Read-` | Reads a template from disk |
| `Measure-` | Counts |
| `Connect-` / `Disconnect-` | Authentication — without these the mode could not sign in |
| `Find-` / `Search-` / `Resolve-` / `Show-` / `Compare-` | Look-ups and inspection |
| `Format-` | Shapes output |
| `Write-` | Local log output only (`Write-PnPTraceLog`) |

Pipeline shaping is allowed too, since these appear in the parsed script as commands: `Select-`,
`Where-`, `Sort-`, `Group-`, `ForEach-`, `Out-`, `Join-`, `Split-`. Without them, even
`Get-PnPList | Select-Object Title` would be refused.

### Refused verbs

| Verb | Why |
| --- | --- |
| `Set-` / `Add-` / `New-` / `Update-` / `Rename-` | Creates or changes objects |
| `Remove-` / `Clear-` / `Reset-` / `Restore-` / `Move-` / `Copy-` | Destroys, overwrites or relocates |
| `Enable-` / `Disable-` / `Grant-` / `Revoke-` / `Deny-` / `Approve-` | Changes access or state |
| `Invoke-` / `Start-` / `Stop-` / `Restart-` / `Submit-` / `Send-` / `Sync-` / `Request-` / `Receive-` | Triggers actions with side effects |
| `Import-` / `Save-` / `Publish-` / `Unpublish-` / `Register-` / `Unregister-` | Applies content or registrations |
| `Install-` / `Uninstall-` / `Merge-` / `Repair-` / `Unlock-` / `Undo-` / `Use-` | Changes tenant or app state |

### Also refused

- **Commands invoked indirectly** (`& $someVariable`), because what they would run cannot be
  established before they run.
- **Native executables** (`pwsh`, `git`, ...), which have no verb to classify.
- **Method calls that can change state** — anything named `Delete*`, `Recycle*`
  and `Execute*`. `ExecuteQuery` is the commit point for every CSOM change, so
  `$list.DeleteObject(); $ctx.ExecuteQuery()` is refused even though neither is a cmdlet. Read-only
  helpers such as `ToString()` and `Trim()` are unaffected.

### Limits worth knowing

- Read-only refers to **Microsoft 365**. Local file output (`Out-File`, `Export-*`) is still allowed.
- Classification is by verb, so a cmdlet whose verb does not match its behaviour is classified by the
  verb. `Invoke-*` is refused wholesale for this reason.
- This is defence in depth, not a sandbox. A script that builds a command name at runtime is refused
  rather than analysed, but static analysis cannot prove the absence of every escape.

## Destructive Commands

Commands using a destructive verb — `Remove-*`, `Clear-*`, `Reset-*`, `Uninstall-*`, `Revoke-*`,
`Deny-*`, `Restore-*`, `Move-*`, `Rename-*`, `Disable-*` — are **not run without confirmation**.
Neither is a command invoked indirectly, since it cannot be identified in advance.

This check favours asking too often over missing something: it also matches a destructive name that
appears only as text (for example inside a string), so you may occasionally be asked to confirm a
command that turns out to be harmless.

- On clients that support prompting, you will be asked to confirm the exact command first.
- On clients that do not, the command is blocked and must be re-sent with `confirmDestructive: true`.
  Always show the user the exact command and get a real answer before doing that.
- Set `PNP_MCP_CONFIRM_DESTRUCTIVE=false` to disable the check entirely (not recommended outside
  automation where the commands are already reviewed).

## Authentication Best Practices

### Connect to SharePoint Online

Establish a connection once per session; it persists across later commands:

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
