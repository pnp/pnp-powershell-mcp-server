# PnP PowerShell MCP Server

## 💡 Description

This MCP server allows the use of natural language to run [PnP PowerShell](https://pnp.github.io/powershell/) commands and to author complex PnP PowerShell scripts. It may handle complex prompts that are executed as a chain of PnP PowerShell cmdlets that try to fulfill the user's request, and it can search the community's [PnP Script Samples](https://pnp.github.io/script-samples/) library for ready-to-adapt scripts. This way you can manage many different areas of Microsoft 365 — SharePoint Online, Microsoft Teams, Entra ID, OneDrive, Planner, Power Platform, Microsoft 365 Groups, taxonomy, search, and tenant administration — straight from your MCP client, and use it as a jump-start for writing your own automation scripts.

## 📦 Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (only required to build/run from source — published tool releases are self-contained)
- [PowerShell 7+](https://aka.ms/powershell) (`pwsh`) installed and available on `PATH`
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
"Add a new list to this site with title 'awesome ducks'. Then add new columns to that list including them in the default view. The first should be a text description column and the second one should be a user column. Then add 3 items to this list with some funny jokes about ducks added in the description column and my user in the user column."

### Manage Microsoft Teams

prompt:
"Create a new Team on Teams with name 'Awesome Ducks' and in the General channel add a welcome post."

### Bootstrap a script from a community sample

prompt:
"I need a PnP PowerShell script that exports all SharePoint list items to a CSV file — find a community sample and adapt it for the 'Documents' list on my site."

### Report on tenant state

prompt:
"Can you check if I have a Power Automate flow called 'HoursReportingReminder' and if so disable it?"

## 🛠️ Tools

| Tool | Description |
| --- | --- |
| pnp_search_commands | Searches PnP PowerShell commands using keyword matching against command names, verbs, and nouns. Use this tool first to find relevant commands. |
| pnp_get_command_docs | Gets detailed documentation for a specific PnP PowerShell command including syntax, parameters, and examples. |
| pnp_run_command | Executes one or more PnP PowerShell commands and returns the result. Runs in a persistent session, so a `Connect-PnPOnline` connection is reused across calls. Destructive commands require confirmation first. |
| pnp_get_connection_status | Checks the current PnP PowerShell connection status before running commands. |
| pnp_reset_session | Ends a session and its PnP connection. Use it to sign out, switch accounts, or recover a session that has stopped responding. |
| pnp_get_best_practices | Returns recommended best practices for using PnP PowerShell via this MCP server, including authentication, sessions, error handling, and execution tips. |
| pnp_search_script_samples | Searches the community [PnP Script Samples](https://pnp.github.io/script-samples/) index for scripts matching a keyword or use case. |
| pnp_get_script_sample | Retrieves the full PnP PowerShell script code for a specific script sample by name, fetched live from GitHub. |
| pnp_suggest_script | Finds the most relevant community script samples for a task and returns their full script code plus adaptation guidance, in one call. |

### Sessions

Commands run in a persistent `pwsh` session, so a connection made with `Connect-PnPOnline` stays
alive across tool calls — you connect once rather than on every command. Pass an optional `sessionId`
to `pnp_run_command` / `pnp_get_connection_status` when you need two tenant connections side by side;
otherwise leave it unset. Idle sessions end after 30 minutes.

### Configuration

| Environment variable | Default | Description |
| --- | --- | --- |
| `PNP_MCP_COMMAND_TIMEOUT_SECONDS` | `600` | Wall-clock limit for a single `pnp_run_command` call. On timeout the session is terminated and the connection is lost. |
| `PNP_MCP_CONFIRM_DESTRUCTIVE` | `true` | Set to `false` to run destructive commands (`Remove-*`, `Clear-*`, ...) without asking for confirmation. |
| `PNP_SCRIPT_SAMPLES_PATH` | _(unset)_ | Path to a local clone of the PnP script samples repository, used as a fallback when GitHub is unreachable. |

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

If you need to point the script-sample tools at a local clone of [pnp/script-samples](https://github.com/pnp/script-samples) instead of the auto-discovered VS Code extension index, set the `PNP_SCRIPT_SAMPLES_PATH` environment variable to the clone's root folder.

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
