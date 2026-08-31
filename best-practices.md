# Best Practices for Using PnP PowerShell via MCP Server

This guide provides best practices for using PnP PowerShell commands through the MCP server, including authentication, error handling, and execution tips.

## Recommended Workflow

Use this flow for reliable execution:

1. **Check you can run anything at all** with `pnp_diagnose_connection`, passing `targetUrl` when you
   know which site the task is about. Make this your first call in a new session: it answers, in one
   round trip, whether `pwsh` is on `PATH`, whether the `PnP.PowerShell` module is installed, what
   connection the session holds, and — when there is none — which app registration or persisted login
   this machine can sign in with. Every failing check names its cause and the exact next command, with
   no placeholder left in it, so **run the command it gives you rather than composing one**. Use
   `pnp_get_connection_status` instead when you only need to re-check the connection on a session you
   have already diagnosed.
2. **Search commands** with `pnp_search_commands` to find the right command for your task.
3. **Read documentation** with `pnp_get_command_docs` to understand syntax, parameters, and examples. Both this and `pnp_search_commands` return the cmdlet's published documentation URL, which is worth citing to the user and often carries examples the shipped help omits.
4. **Search community samples** with `pnp_search_script_samples` or `pnp_suggest_script` before writing a script from scratch — there is a good chance someone has already solved a similar problem.
5. **Execute commands** with `pnp_run_command` in small, verifiable steps.

This guide is long, so `pnp_get_best_practices` accepts an optional `section` — `workflow`, `docs`,
`sessions`, `config`, `readonly`, `output`, `destructive`, `auth`, `execution` or `patterns` — to return one
topic instead of everything. Pull `readonly` when a command is refused, `destructive` before a
confirmation prompt, or `patterns` when looking for a worked example.

## Finding More About a Cmdlet

Every cmdlet is documented on <https://pnp.github.io/powershell/>, and `pnp_get_command_docs` returns
two links to that page before the help text:

```text
MARKDOWN DOCUMENTATION (prefer this — the same page in source form, at a fraction of the tokens): https://raw.githubusercontent.com/pnp/powershell/dev/documentation/Get-PnPWeb.md
HTML DOCUMENTATION: https://pnp.github.io/powershell/cmdlets/Get-PnPWeb.html
```

**Fetch the markdown one.** It is the source the HTML page is generated from, so it says the same
thing without the site chrome, navigation and styling that make the rendered page several times more
expensive to read. Give the *user* the HTML link, since that is the page they can browse.

The local help returned by `pnp_get_command_docs` is generated from the installed module, so it can
lag the published page and sometimes omits examples entirely. Reach for the links when the local help
is not enough.

### Worked example

You need to filter list items server-side and `Get-PnPListItem`'s local help does not explain `-Query`:

1. Find the cmdlet:

   ```jsonc
   // pnp_search_commands
   { "query": "list item" }
   ```

   ```jsonc
   // structuredContent, ranked most relevant first. `count` always equals commands.length.
   { "query": "list item", "count": 20, "indexedModuleVersion": "3.4.1",
     "commands": [
       { "name": "Get-PnPListItem", "verb": "Get", "noun": "PnPListItem",
         "synopsis": "Retrieves list items",
         "parameters": ["List", "Id", "UniqueId", "Query", "PageSize", "Connection"],
         "docsUrl": "https://pnp.github.io/powershell/cmdlets/Get-PnPListItem.html" }
       // ... 19 more, elided here
     ] }
   ```

   The parameter names are a shortlist, not the full syntax — read the docs before calling.

2. Read the local help:

   ```jsonc
   // pnp_get_command_docs
   { "commandName": "Get-PnPListItem" }
   ```

   The output begins with:

   ```text
   MARKDOWN DOCUMENTATION (prefer this ...): https://raw.githubusercontent.com/pnp/powershell/dev/documentation/Get-PnPListItem.md
   HTML DOCUMENTATION: https://pnp.github.io/powershell/cmdlets/Get-PnPListItem.html
   ```

3. If the parameter is still unclear, **fetch the markdown URL with whatever web-fetch tool the client
   provides** and read the parameter and examples sections. If no fetch tool is available, give the
   user the HTML link rather than guessing at the syntax.

4. Build the command from what the page shows, then run it in a small verifiable step.

### When there is no link

The two links above come from an index vendored into this server, so they are there even when `pwsh`
or the module is not. A cmdlet newer than this build is not in that index; for those,
`pnp_get_command_docs` falls back to the `HelpUri` your installed module reports, and search results
carry a `HelpUri` field. When neither is available it almost always means an **older `PnP.PowerShell`
build** — current versions populate `HelpUri` for effectively every cmdlet — and the tool says so and
offers the fallback directly in its output.

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
- **`pnp_diagnose_connection` checks both of these**, plus what connection the session holds, in one
  call. Every failing check names its cause and the exact next command. The `pwsh` and module checks
  need no tenant and no network, so they still answer on a machine that is not set up yet — which is
  exactly when they are useful. Once a connection exists, inspecting it asks PnP for a Graph token,
  so that part does reach Entra ID.

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
  visible in the other. `pnp_search_commands` uses no session at all, and `pnp_get_command_docs`
  always uses `default`, since cmdlet lookup does not depend on the connection.
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
| `PNP_SCRIPT_SAMPLES_PATH` | _(unset)_ | Local clone of the script samples repo, overriding the vendored index. |

Both booleans are matched exactly: read-only turns on only for the literal `true`, and confirmation
turns off only for the literal `false`. `1` and `yes` leave the default in place.

## Output Size

Tool responses are capped (50,000 characters by default). What happens past the cap depends on the
shape of the result.

### A large result set is summarised and paged, not cut

When `pnp_run_command` produces a JSON array too big for the cap, you get the true row count, the
field names, and as many whole rows as fit — followed by a cursor:

```text
Result set: 223 rows, summarised because the full output is 732,239 characters and the cap is 50,000.
Fields: Url, Title, Template, StorageQuota, LastContentModifiedDate, and 77 more
Rows 1-14 of 223:
[ ... ]
MORE: 209 rows remain. Call 'pnp_get_result_page' with cursor 'a1b2c3d4e5' and offset 14.
```

Every page is complete, valid JSON, and the count is the real one — 223 rows exist whether or not you
read them all. Call `pnp_get_result_page` with the cursor to continue; it pages over rows already
fetched, so it costs nothing against the tenant and returns exactly what the original command saw.

The result set is held in the session that produced it, and **the next command in that session
replaces it**. Page through what you need before running anything else. Re-running the command is the
only way to get fresher rows.

### Everything else is still truncated

Non-array output — one large object, a `Format-Table` dump, a long help topic — is cut at a line
boundary with `[output truncated: N of M characters omitted]`.

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

- On clients that support prompting, you will be asked to confirm the exact command first. The
  approval is bound to that exact command text, so a retry carrying different arguments prompts again.
- On clients that do not, the command is blocked and **there is no way to approve it from inside the
  conversation**. There is deliberately no tool parameter that asserts approval on the user's behalf:
  the model is the party being gated, so a gate the model can switch off is not a gate. Show the user
  the command and let them run it themselves, or use a client that supports MCP elicitation.
- Set `PNP_MCP_CONFIRM_DESTRUCTIVE=false` to disable the check entirely. This is an operator decision
  taken outside the conversation, and is not recommended outside automation where the commands are
  already reviewed.

### What is not gated, and why

**An ordinary mutating verb does not prompt.** `Set-*`, `Add-*`, `New-*`, `Enable-*` and `Grant-*` run
with no confirmation at all, and some of them carry real consequences — `Set-PnPTenant` changes
tenant-wide settings, and `Grant-PnPAzureADAppSitePermission` gives an application access to a site.

This is a deliberate trade-off, not an oversight. Mutating verbs are most of what this server is asked
to do, so prompting on all of them would produce a prompt routine enough to stop being read, which
costs more safety than it buys. The line is drawn at verbs that destroy, overwrite or revoke, because
those are the cases that running the command again with better arguments cannot undo.

The consequence is worth stating plainly: **on a `Set-*` or `Grant-*` command, you are the only
review.** Nobody outside the conversation sees it before it runs. So:

- **Say what it will change before you run it**, in terms the user can check — which tenant, which
  site, which setting, from what to what.
- **Read the current value first.** `Get-PnPTenant` before `Set-PnPTenant` gives the user something to
  compare against, and gives you something to restore from.
- **Change one thing per command**, against a scope you have already confirmed, rather than a chain
  that leaves a partial change behind when a later step fails.
- **Prefer `PNP_MCP_READONLY=true` when the task only needs to read.** It refuses every mutating verb
  outright, which is a stronger guarantee than any prompt.

## Authentication Best Practices

### Ask before you connect

**Run `pnp_diagnose_connection` with the site you are targeting, and run the command it gives you.**

```jsonc
// pnp_diagnose_connection
{ "targetUrl": "https://contoso.sharepoint.com/sites/marketing" }
```

Section 4 of the report says what this machine can actually authenticate with — persisted logins, a
cached token, `ENTRAID_APP_ID` / `ENTRAID_CLIENT_ID`, a certificate path — and its `NEXT STEP` is a
complete command with nothing left to fill in.

**Do not compose a connect from memory, and never assume an environment variable is set.** Since
September 2024 `-ClientId` is required for the interactive, credentials and OS-login flows, so a connect
without one works only when a persisted login or one of those variables supplies it. The report tells you
which of those exist here, so there is nothing left to guess.

### The first sign-in is not yours to run

A first-time sign-in opens a browser and waits for a person. That prompt is invisible from inside this
conversation, so the call blocks until it times out and nothing gets connected.

So when the report says `BLOCKED`, hand the commands to the user instead of running them:

```powershell
# A person signing in: register an app, then connect once
Register-PnPEntraIDAppForInteractiveLogin -ApplicationName "PnP PowerShell" -Tenant contoso.onmicrosoft.com -Interactive
Connect-PnPOnline -Url https://contoso.sharepoint.com/sites/marketing -ClientId <app id> -PersistLogin

# Unattended instead: register an app with a certificate, then use it
Register-PnPEntraIDApp -ApplicationName "PnP PowerShell" -Tenant contoso.onmicrosoft.com -OutPath . -DeviceLogin
Connect-PnPOnline -Url https://contoso.sharepoint.com -ClientId <app id> -Tenant contoso.onmicrosoft.com `
  -CertificatePath .\PnP-PowerShell.pfx -CertificatePassword (Read-Host -AsSecureString)
```

Both registration cmdlets need an administrator to consent before the app works.

`-PersistLogin` is the part that matters for the interactive path. It records the app id against that
tenant and caches the token, so afterwards this server connects with **no client id, no browser and no
prompt** — which is why the report can name a placeholder-free command at all:

```powershell
Connect-PnPOnline -Url https://contoso.sharepoint.com/sites/marketing
```

### Choosing a method

| Situation | Method |
| --- | --- |
| The report names a persisted login | `Connect-PnPOnline -Url <site>` — nothing else needed |
| A client id is available, tenant not yet persisted | add `-ClientId <id> -PersistLogin` |
| Automation, no person present | `-ClientId -Tenant -CertificatePath -CertificatePassword`, or `-Thumbprint` for a certificate already in the Windows store |
| Hosted in Azure | `-ManagedIdentity` (Azure Functions, Automation runbooks, Cloud Shell only) |
| Nothing available | hand the user the commands above; it cannot be done from here |

- `-ClientId` and `-Tenant` are both mandatory for certificate auth, and a `.pfx` normally needs
  `-CertificatePassword` as a `SecureString`.
- **Never put a credential in a script.** Use a certificate, a managed identity, or environment variables.
- A device login is the one method PnP will not elevate to the admin site, so tenant-wide cmdlets refuse
  rather than return 403. Connect straight to `https://<tenant>-admin.sharepoint.com` for those.
- Changing an app registration's permissions does **not** refresh a persisted token. Run
  `Disconnect-PnPOnline -ClearPersistedLogin` and sign in again, or the old scopes keep being used.
- `AADSTS50173`, `AADSTS700082` and `invalid_grant` all mean the cached credential is dead. **Retrying
  changes nothing** — it has to be cleared and signed in again.
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
