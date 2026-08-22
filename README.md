# PnP PowerShell MCP Server

## 💡 Description

This MCP server allows the use of natural language to run [PnP PowerShell](https://pnp.github.io/powershell/) commands and to author complex PnP PowerShell scripts. It may handle complex prompts that are executed as a chain of PnP PowerShell cmdlets that try to fulfill the user's request, and it can search the community's [PnP Script Samples](https://pnp.github.io/script-samples/) library for ready-to-adapt scripts. This way you can manage many different areas of Microsoft 365 — SharePoint Online, Microsoft Teams, Entra ID, OneDrive, Planner, Power Platform, Microsoft 365 Groups, taxonomy, search, and tenant administration — straight from your MCP client, and use it as a jump-start for writing your own automation scripts.

## 📦 Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (only required to build/run from source — published tool releases are self-contained)
- [PowerShell 7.4 or above](https://aka.ms/powershell) (`pwsh`) installed and available on `PATH`
- The [`PnP.PowerShell`](https://www.powershellgallery.com/packages/PnP.PowerShell) module installed:

  ```powershell
  Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force
  ```

## 🚀 Installation & Usage

This MCP server shells out to the locally installed [PnP PowerShell](https://pnp.github.io/powershell/) module — it does not do any authentication for you. Authenticate first using `Connect-PnPOnline` (see [Best Practices](./best-practices.md) for the recommended auth methods), then the MCP server will reuse the same PnP PowerShell connection context.

- **TYPE**: `Local` (stdio)
- **INSTALL**: [![Install PnP PowerShell MCP in VS Code](https://img.shields.io/badge/VS_Code-0098FF?style=flat-square&logo=visualstudiocode&logoColor=white)](https://insiders.vscode.dev/redirect/mcp/install?name=pnp-powershell&config=%7B%22type%22%3A%22stdio%22%2C%22command%22%3A%22pnp-powershell-mcp-server%22%7D) [![Install PnP PowerShell MCP in VS Code Insiders](https://img.shields.io/badge/VS_Code_Insiders-24bfa5?style=flat-square&logo=visualstudiocode&logoColor=white)](https://insiders.vscode.dev/redirect/mcp/install?name=pnp-powershell&config=%7B%22type%22%3A%22stdio%22%2C%22command%22%3A%22pnp-powershell-mcp-server%22%7D&quality=insiders) [![Install PnP PowerShell MCP in Visual Studio](https://img.shields.io/badge/Visual_Studio-C16FDE?style=flat-square&logo=visualstudio&logoColor=white)](https://aka.ms/vs/mcp-install?%7B%22name%22%3A%22pnp-powershell%22%2C%22type%22%3A%22stdio%22%2C%22command%22%3A%22pnp-powershell-mcp-server%22%7D) [![Install PnP PowerShell MCP in Cursor](https://img.shields.io/badge/Cursor-000000?style=flat-square&logo=cursor&logoColor=white)](https://cursor.com/install-mcp?name=pnp-powershell&config=eyJ0eXBlIjoic3RkaW8iLCJjb21tYW5kIjoicG5wLXBvd2Vyc2hlbGwtbWNwLXNlcnZlciJ9) [![Install PnP PowerShell MCP in Claude Code](https://img.shields.io/badge/Claude_Code-Install-orange?style=flat-square&logo=claude&logoColor=white)](#add-to-claude-code)

> The one-click buttons above register the server under the name `pnp-powershell` and point it at the `pnp-powershell-mcp-server` command, so **install the tool first** (below) — otherwise the client will register a server it cannot start.

### Install as a .NET global tool

```bash
dotnet tool install --global PnP.PowerShell.MCPServer --prerelease
```

This installs a self-contained, native AOT executable named `pnp-powershell-mcp-server` on your `PATH`. Supported platforms: Windows (x64, arm64), macOS (arm64, x64) and Linux (x64, arm64, musl x64).

To update an existing install:

```bash
dotnet tool update --global PnP.PowerShell.MCPServer --prerelease
```

> **Hitting `Version <x> of package PnP.PowerShell.MCPServer.<rid> is not found in NuGet feeds`?**
> This tool ships as a small wrapper package plus one package per platform, and that error means the platform package for your machine was never published for that version. It affects `0.1.1-beta` and earlier — install `0.1.3-beta` or later, or [build and run from source](#-how-to-build-and-run-it-locally). Maintainers: see [RELEASING.md](./RELEASING.md).

### Add to VS Code

1. Open the Command Palette (Ctrl+Shift+P or Cmd+Shift+P on macOS) and type `MCP: Add Server`.
2. Select `Command (stdio)` as the server type.
3. Enter the command to run the MCP server:

   ```text
   pnp-powershell-mcp-server
   ```

4. Name the server (e.g., `PnP PowerShell MCP Server`).

As a result, you should have the following configuration in your `.vscode/mcp.json` file:

```json
{
    "servers": {
        "PnP PowerShell MCP Server": {
            "type": "stdio",
            "command": "pnp-powershell-mcp-server"
        }
    }
}
```

Now when you open the GitHub Copilot chat in VS Code, you should be able to select the `PnP PowerShell MCP Server` from the list of available MCP servers and start using it to manage Microsoft 365 using natural language. In the prompt specify that "Using PnP PowerShell, I want you to..." and GitHub Copilot Agent will use the MCP server to execute your request.

### Add to GitHub Copilot CLI

If you are using [GitHub Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/about-copilot-cli), you may add the PnP PowerShell MCP server to Copilot by doing the following:

1. Start the [Copilot CLI](https://www.npmjs.com/package/@github/copilot):

   ```bash
   copilot
   ```

2. Use the copilot mcp command to add the MCP server:

   ```text
   /mcp add
   ```

3. Fill in the MCP form:
   - Server name: whatever you like, without spaces, e.g. `pnp-powershell-mcp-server`
   - Server type: `Local`
   - Command: `pnp-powershell-mcp-server`
   - Arguments: leave empty

After that click `Ctrl+S` to save and `q` to exit the MCP form. You can now use the PnP PowerShell MCP server in GitHub Copilot CLI, e.g. "Using PnP PowerShell, I want you to...".

### Add to Claude Code

```bash
claude mcp add pnp-powershell --scope user -- pnp-powershell-mcp-server
```

`--scope user` makes the server available in every project; drop it to register it for the current project only. Check it was picked up with `claude mcp list`.

### Add to Claude Desktop

1. In Claude Desktop, open Settings by clicking on the hamburger icon in the top left corner.
2. Select File > Settings (or press `Ctrl + ,`).
3. In the Developer tab, click Edit Config.
   Note: If you don't see the Developer tab, enable it first from Help > Enable Developer Mode.
4. This opens explorer; edit `claude_desktop_config.json` in your favorite text editor and add:

   ```json
   {
     "mcpServers": {
       "PnP-PowerShell": {
         "command": "pnp-powershell-mcp-server"
       }
     }
   }
   ```

5. Restart Claude Desktop for the changes to take effect.

> Note: On Windows, Claude doesn't exit when you close the window — it keeps running in the background. Find it in the system tray, right-click and select Quit to exit completely.

### Add to Cursor

1. From the chat option pick the `Agent settings` option.
2. Go to `Tools & MCP` tab and click on `New MCP server`.
3. Modify the `mcp.json` configuration as follows:

   ```json
   {
     "mcpServers": {
       "PnP PowerShell MCP Server": {
         "type": "stdio",
         "command": "pnp-powershell-mcp-server"
       }
     }
   }
   ```

4. Save and enable the `PnP PowerShell MCP Server` in the `Tools & MCP` tab and wait for the tools to load.

## 📷 Use Cases

The below use cases are only a few examples of how you may use this MCP server. It is capable of handling many different tasks, so feel free to experiment and manage Microsoft 365 using natural language.

### Manage SharePoint Online

prompt:
```text
Add a new list to this site with title 'awesome ducks'. Then add new columns to that list including them in the default view. The first should be a text description column and the second one should be a user column. Then add 3 items to this list with some funny jokes about ducks added in the description column and my user in the user column.
```

### Manage Microsoft Teams

prompt:
```text
Create a new Team on Teams with name 'Awesome Ducks' and in the General channel add a welcome post.
```

### Bootstrap a script from a community sample

prompt:
```text
I need a PnP PowerShell script that exports all SharePoint list items to a CSV file — find a community sample and adapt it for the 'Documents' list on my site.
```

### Report on tenant state

prompt:
```text
Can you check if I have a Power Automate flow called 'HoursReportingReminder' and if so disable it?
```

## 🛠️ Tools

| Tool | Description |
| --- | --- |
| pnp_search_commands | Finds which cmdlet does a job, by keyword against cmdlet names, verbs and nouns. Each result carries the cmdlet's `HelpUri`. Falls back to a vendored cmdlet index when `pwsh` or the module is unavailable, so it still answers on a machine that is not set up yet. |
| pnp_get_command_docs | Gets the reference documentation for one named cmdlet — syntax, parameters, parameter sets and examples — preceded by links to both the raw markdown source of its documentation page and the rendered HTML page. The markdown is the same content for a fraction of the tokens. |
| pnp_run_command | Runs PnP PowerShell against the connected tenant and returns the result. Runs in a persistent session, so a `Connect-PnPOnline` connection is reused across calls. Destructive commands require confirmation first. A result set too large for the output cap is summarised and paged rather than truncated. |
| pnp_get_result_page | Returns the next page of a result set `pnp_run_command` summarised. Pages over rows already fetched, so it costs nothing against the tenant and returns exactly the rows the original command saw. |
| pnp_get_connection_status | Checks whether the session is signed in, to which site, and as which account. |
| pnp_diagnose_connection | Checks everything that has to be true before a command can run: `pwsh` on `PATH`, the `PnP.PowerShell` module, and what connection the session holds. Every failing check names its cause and the exact next command. The `pwsh` and module checks need no tenant and no network, so it still works on a machine that is not set up yet; once a connection exists it also inspects that connection, which asks PnP for a Graph token and so reaches Entra ID. |
| pnp_reset_session | Ends a session and its PnP connection. Use it to sign out, switch accounts, or recover a session that has stopped responding. |
| pnp_get_best_practices | Returns best practices for using PnP PowerShell via this MCP server. Takes an optional `section` (`workflow`, `docs`, `sessions`, `config`, `readonly`, `output`, `destructive`, `auth`, `execution`, `patterns`) to retrieve one topic instead of the whole guide, which keeps the response small. |
| pnp_search_script_samples | Lists community [PnP Script Samples](https://pnp.github.io/script-samples/) matching a keyword — titles, descriptions and links, no code. Answers from an index compiled into the server, so it needs no network. |
| pnp_get_script_sample | Retrieves the full PnP PowerShell script code for one named script sample. The index entry is local; the script body is fetched from GitHub. |
| pnp_suggest_script | Finds the most relevant community script samples for a task and returns their full script code plus adaptation guidance, in one call. |

Every tool declares its `readOnlyHint`, `idempotentHint` and `openWorldHint` annotations, and the two
that can change state also declare `destructiveHint`, so a client can decide what to auto-approve
without guessing.

Tool descriptions are gated on whether they actually select: `ToolSelectionEvaluatorTests` scores every
prompt in [e2eTestPrompts.md](./tests/PnPPowerShell.MCPServer.Tests/e2eTestPrompts.md) against the
published descriptions and fails the build if the right tool is not ranked in the top three. See
[Tool selection](#tool-selection).

### 📚 Resources

The same guidance and cmdlet documentation is also exposed as MCP **resources**, so a client that
supports them can browse and cache the content instead of spending a tool call on it.

| URI | Contents |
| --- | --- |
| `pnp://best-practices` | The whole guidance document. |
| `pnp://best-practices/{section}` | One section: `workflow`, `docs`, `sessions`, `config`, `readonly`, `output`, `destructive`, `auth`, `execution`, `patterns`. |
| `pnp://cmdlet/{name}` | Help text for one cmdlet, preceded by its published documentation URL — e.g. `pnp://cmdlet/Get-PnPWeb`. |

### Sessions and `sessionId`

Commands run in a persistent `pwsh` session, so a connection made with `Connect-PnPOnline` stays
alive across tool calls — you connect once rather than on every command.

**You normally never set `sessionId`.** Leave it out and everything shares the session named
`default`. It exists for one situation: working against **two tenants (or two accounts) at the same
time**, because a single PnP session can only hold one connection.

| | Without `sessionId` | With `sessionId` |
| --- | --- | --- |
| Session used | `default` | the name you pass |
| Connection | one, shared | one per session name |
| Variables (`$sites`, ...) | shared | isolated per session |

Three tools accept it: `pnp_run_command`, `pnp_get_connection_status` and `pnp_reset_session`. The
metadata tools (`pnp_search_commands`, `pnp_get_command_docs`) always use `default`, since looking up
a cmdlet does not depend on which tenant you are connected to.

#### When to use it

You are asking the agent for something in natural language, so you set this by *saying* it rather
than by editing config. Two tenants in one conversation:

```text
Connect to contoso in a session called "contoso" and to fabrikam in a session called "fabrikam",
then list the site count in each and tell me which is larger.
```

The agent then makes calls equivalent to:

```jsonc
// tool: pnp_run_command
{ "sessionId": "contoso",  "command": "Connect-PnPOnline -Url https://contoso.sharepoint.com  -Interactive" }
{ "sessionId": "fabrikam", "command": "Connect-PnPOnline -Url https://fabrikam.sharepoint.com -Interactive" }
{ "sessionId": "contoso",  "command": "(Get-PnPTenantSite).Count" }
{ "sessionId": "fabrikam", "command": "(Get-PnPTenantSite).Count" }
```

For everything else — including multi-step work against a single tenant — omit it:

```text
Connect to contoso, find all site collections with no owner, and export them to a CSV.
```

#### Things worth knowing

- **Sign out or switch account** with `pnp_reset_session`. It ends that session and discards its
  connection and variables; the next call starts fresh.
- **Idle sessions end after 30 minutes.** A session busy running a command is never reclaimed, however
  long it takes — just reconnect if one does expire.
- **One command at a time per session.** A second call against a busy session waits, then reports the
  session is busy. To genuinely run two things at once, use two different `sessionId` values.
- **Reuse the connection.** Do not re-run `Connect-PnPOnline` before every command; check
  `pnp_get_connection_status` first. It reports which session it inspected.

### Configuration

| Environment variable | Default | Description |
| --- | --- | --- |
| `PNP_MCP_COMMAND_TIMEOUT_SECONDS` | `600` | Wall-clock limit for a single `pnp_run_command` call. On timeout the session is terminated and the connection is lost. |
| `PNP_MCP_CONFIRM_DESTRUCTIVE` | `true` | Set to `false` to run destructive commands (`Remove-*`, `Clear-*`, ...) without asking for confirmation. This is the only way to bypass the gate: there is no tool parameter that lets the model approve its own destructive command, so on a client that cannot show a confirmation prompt, destructive commands are simply blocked. |
| `PNP_MCP_READONLY` | `false` | Set to `true` to refuse any command that would change Microsoft 365. Allowed verbs: `Get-`, `Export-`, `Test-`, `Convert-`/`ConvertTo-`/`ConvertFrom-`, `Read-`, `Measure-`, `Connect-`/`Disconnect-`, `Find-`, `Format-`, `Resolve-`, `Write-`, `Search-`, `Show-`, `Compare-`, plus pipeline shaping (`Select-`, `Where-`, `Sort-`, `Group-`, `ForEach-`, `Out-`, `Join-`, `Split-`). Refused: `Set-`, `Remove-`, `Add-`, `New-`, `Clear-`, `Invoke-`, `Update-`, `Move-`, `Enable-`/`Disable-`, `Grant-`/`Revoke-`, `Copy-`, `Import-`, `Restore-`, `Reset-`, `Rename-`, `Start-`/`Stop-`, `Register-`/`Unregister-`, and every other change verb — along with indirectly invoked commands, native executables, and state-changing method calls such as `ExecuteQuery`. See [Best Practices](./best-practices.md#read-only-mode) for the full table. Local file output (`Out-File`, `Export-*`) is still permitted. |
| `PNP_MCP_MAX_OUTPUT_CHARS` | `50000` | Largest tool response returned, in characters. A JSON result set over the cap is summarised — true row count, field names, and as many whole rows as fit, plus a cursor for `pnp_get_result_page` — so the response stays complete and parseable. Anything else is truncated to its first whole lines with a note saying how much was dropped. Values below 2000 are ignored, since the note itself would leave no room for output. |
| `PNP_SCRIPT_SAMPLES_PATH` | _(unset)_ | Path to a local clone of [pnp/script-samples](https://github.com/pnp/script-samples), overriding the index compiled into the server. For contributors working against a newer catalogue than the one this build shipped with. |

The client passes the environment in when it launches the server process, so where you set them decides
both who they apply to and that a **server restart** is needed for a change to take effect.

#### Where to set them

**In your MCP client config** — the usual choice. This is the only place that applies to the server no
matter how the client was launched, and it survives a reboot.

<details>
<summary>VS Code — <code>.vscode/mcp.json</code> (or the user-level <code>mcp.json</code>)</summary>

```json
{
    "servers": {
        "PnP PowerShell MCP Server": {
            "type": "stdio",
            "command": "pnp-powershell-mcp-server",
            "env": {
                "PNP_MCP_READONLY": "true",
                "PNP_MCP_COMMAND_TIMEOUT_SECONDS": "1800"
            }
        }
    }
}
```
</details>

<details>
<summary>Claude Desktop — <code>claude_desktop_config.json</code></summary>

```json
{
  "mcpServers": {
    "PnP-PowerShell": {
      "command": "pnp-powershell-mcp-server",
      "env": {
        "PNP_MCP_READONLY": "true"
      }
    }
  }
}
```
</details>

<details>
<summary>Cursor — <code>mcp.json</code></summary>

```json
{
  "mcpServers": {
    "PnP PowerShell MCP Server": {
      "type": "stdio",
      "command": "pnp-powershell-mcp-server",
      "env": {
        "PNP_MCP_READONLY": "true"
      }
    }
  }
}
```
</details>

<details>
<summary>Claude Code — <code>claude mcp add</code></summary>

```bash
claude mcp add pnp-powershell --scope user \
  --env PNP_MCP_READONLY=true \
  --env PNP_MCP_COMMAND_TIMEOUT_SECONDS=1800 \
  -- pnp-powershell-mcp-server
```
</details>

**In your shell**, when you want a one-off run — for example to try read-only mode without editing
config. The client must be started *from that shell* for it to inherit the value:

```bash
# macOS / Linux
PNP_MCP_READONLY=true code .
```

```powershell
# Windows PowerShell
$env:PNP_MCP_READONLY = 'true'; code .
```

**Machine-wide**, if every tool on the box should behave the same way. Note this affects other
processes too, so prefer the client config unless that is what you want:

```powershell
# Windows, persists across reboots
[Environment]::SetEnvironmentVariable('PNP_MCP_READONLY', 'true', 'User')
```

#### Worked examples

| Goal | Setting |
| --- | --- |
| Let an agent explore a production tenant without being able to change it | `PNP_MCP_READONLY=true` |
| Tenant-wide reports that take longer than 10 minutes | `PNP_MCP_COMMAND_TIMEOUT_SECONDS=3600` |
| Unattended automation where the commands are already reviewed | `PNP_MCP_CONFIRM_DESTRUCTIVE=false` |
| Work against a script-samples clone newer than the vendored index | `PNP_SCRIPT_SAMPLES_PATH=C:\src\script-samples` |

After changing any of these, **restart the MCP server** (in most clients, reload the window or toggle
the server off and on) — the client passes the environment in when it launches the process, so an
already-running server keeps the old values.

Two cautions: `PNP_MCP_CONFIRM_DESTRUCTIVE=false` removes the only thing standing between an agent
and `Remove-PnPTenantSite`, so set it only where the commands are reviewed some other way. And both
booleans are matched exactly — `PNP_MCP_READONLY` enables only on the literal string `true`
(case-insensitive), and `PNP_MCP_CONFIRM_DESTRUCTIVE` disables only on `false`; anything else, `1` and
`yes` included, leaves the default in place.

Clients that support the MCP **Tasks** extension can run `pnp_run_command` as a task and poll for the
result, rather than holding the request open for the duration of a long tenant operation.

## 🏗️ How to build and run it locally

Before anything, restore and build the project:

```bash
dotnet build
```

### Running MCP in VS Code from local build

Start the MCP server from source so it may be used by GitHub Copilot Agent. In VS Code GitHub Copilot Agent mode, click the tools icon, select `Add more tools` → `Add MCP server` → `Command (stdio)`, and enter:

```bash
dotnet run --project FULL_PATH_TO_YOUR_PROJECT/PnPPowerShell.MCPServer.csproj
```

Name it however you like. It's recommended to add it to `workspace` scope for testing. This repo's [.mcp.json](./.mcp.json) already contains an equivalent configuration you can adapt.

### Vendored data

Two indexes are compiled into the assembly as embedded resources, so the tools that use them work with
no network, no VS Code extension and no tenant:

| File | Contents | Used by |
| --- | --- | --- |
| [data/script-samples.json](./data/script-samples.json) | The PnP Script Samples catalogue — name, title, description, tags, authors | `pnp_search_script_samples`, `pnp_get_script_sample`, `pnp_suggest_script` |
| [data/pnp-commands.json](./data/pnp-commands.json) | Every `PnP.PowerShell` cmdlet name, with the URL templates for its markdown and HTML documentation | `pnp_get_command_docs`, and `pnp_search_commands` when `pwsh` is unavailable |

Both are generated from [pnp/vscode-pnp-powershell](https://github.com/pnp/vscode-pnp-powershell) and
record the source commit, which every tool that reads them prints — a stale index is visible rather
than silent. Refresh them before a release:

```powershell
pwsh ./build/Update-VendoredData.ps1
```

The script fails rather than guessing if either upstream file stops matching the URL templates.

Two overrides come first, for contributors working against a newer catalogue: the PnP PowerShell VS
Code extension's own `samples.json` if that extension is installed, then `PNP_SCRIPT_SAMPLES_PATH`
pointing at a [pnp/script-samples](https://github.com/pnp/script-samples) clone.

### Tool selection

[e2eTestPrompts.md](./tests/PnPPowerShell.MCPServer.Tests/e2eTestPrompts.md) holds natural-language
prompts per tool. `ToolSelectionEvaluatorTests` ranks every tool against each prompt using BM25 over
the published descriptions — no model, no network, no tenant — and fails if the expected tool is not
in the top three. Ranking is the only thing asserted: a confidence score lived here briefly and was
removed, having never caught a regression. **Adding a tool means adding prompts for it**; the test fails
on any tool with none, and when a prompt regresses the fix is usually the tool's `[Description]`, not
the prompt.

`Bm25_agrees_with_the_model_that_read_the_same_descriptions` is the check on the checker: it compares
BM25s top pick against [modelSelections.md](./tests/PnPPowerShell.MCPServer.Tests/modelSelections.md),
where a language model labelled the same prompts from the published descriptions alone. They agree on
93 %. If that falls, the lexical scorer has stopped predicting selection and it is the scorer that needs
replacing, not the prose.

One counter-intuitive rule, learned the hard way: selection is zero-sum between tools, so broadening a
description to win a prompt costs every other tool. Only more *distinctive* wording helps.

### Protocol tests

`StdioProtocolTests` spawns the built server as a real process and speaks newline-delimited JSON-RPC
to it — `initialize`, `tools/list`, `tools/call` — with a hand-rolled client rather than the SDKs,
so the wire format is exercised rather than the SDK talking to itself. It asserts the tool surface, the
annotations as published, and that the destructive-command gate blocks a client which cannot be
prompted. Everything but that last check is hermetic; run `dotnet build` first, since the tests launch
the servers own build output.

### Recorded-playback tests

Tenant-dependent behaviour is recorded once against a dev tenant and replayed offline forever after, so
CI needs neither `pwsh` nor a tenant. Each fixture is filed under the *operation* it records — `run`
plus the command, `command-docs` plus the cmdlet — rather than a hash of the generated script, so
rewording that script does not silently orphan every fixture. Fixtures live in
[tests/PnPPowerShell.MCPServer.Tests/fixtures](./tests/PnPPowerShell.MCPServer.Tests/fixtures) and are
scrubbed on the way in by `TranscriptScrubber` — tenant hostnames, UPNs, GUIDs, tokens, secrets,
thumbprints and certificate blocks, including inside the base64 payload a command is wrapped in.

To re-record, from a machine with a connected dev tenant:

```powershell
$env:PNP_MCP_RECORD_FIXTURES = '1'
$env:PNP_MCP_RECORD_TENANT_URL = 'https://<tenant>.sharepoint.com/sites/<site>'
$env:PNP_MCP_RECORD_CLIENT_ID  = '<app id>'
dotnet test --filter RecordedPlaybackTests
```

**Read every fixture before committing it.** The scrubber cannot detect a display name in free text,
and a recorded fixture is a tenant data leak waiting to be committed.

### Running MCP from local build using the inspector (Debugging)

One of the ways to test the MCP server is by using the [MCP Inspector](https://github.com/modelcontextprotocol/inspector):

```bash
npx @modelcontextprotocol/inspector dotnet run --project ./PnPPowerShell.MCPServer.csproj
```

Wait for the inspector to start and open it in your browser. You should see the MCP server running, and you can query and execute its tools locally.

### Publishing a native AOT build

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Replace `win-x64` with your target [RuntimeIdentifier](https://learn.microsoft.com/dotnet/core/rid-catalog) (`linux-x64`, `osx-arm64`, etc.). The output is a single native executable with no .NET runtime dependency.

Native AOT needs a platform toolchain: the "Desktop development with C++" workload on Windows, Xcode command line tools on macOS, or `clang` and `zlib1g-dev` on Linux.

### Releasing to NuGet

A release is **eight** packages — a small wrapper plus one per platform — and a plain `dotnet pack` builds only the wrapper. Do not publish by hand; see [RELEASING.md](./RELEASING.md) and use the [Release workflow](./.github/workflows/release.yml).

## Contributing to PnP PowerShell MCP Server

Follow the [getting started contributing](/CONTRIBUTING.md) guidelines to help out. Sharing is caring!

## Supportability and SLA

This library is open-source and community provided library with active community providing support for it. This is not Microsoft provided module so there's no SLA or direct support for this open-source component from Microsoft. For more information about the PnP initiative, check out the official website: [Microsoft 365 & Power Platform Community](https://pnp.github.io).

## 🔗 Resources

- [PnP PowerShell documentation](https://pnp.github.io/powershell/)
- [PnP Script Samples](https://pnp.github.io/script-samples/)
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [MCP servers](https://github.com/modelcontextprotocol/servers?tab=readme-ov-file)
- [MCP inspector](https://github.com/modelcontextprotocol/inspector)
