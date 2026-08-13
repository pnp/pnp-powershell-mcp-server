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

### Install as a .NET global tool

Once published to NuGet:

```bash
dotnet tool install --global PnP.PowerShell.McpServer --prerelease
```

This installs a self-contained, native AOT executable named `pnp-powershell-mcp-server` on your `PATH`.

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
| pnp_run_command | Executes one or more PnP PowerShell commands and returns the result. Can be used repeatedly to accomplish complex multi-step tasks. |
| pnp_get_connection_status | Checks the current PnP PowerShell connection status before running commands. |
| pnp_get_best_practices | Returns recommended best practices for using PnP PowerShell via this MCP server, including authentication, error handling, and execution tips. |
| pnp_search_script_samples | Searches the community [PnP Script Samples](https://pnp.github.io/script-samples/) index for scripts matching a keyword or use case. |
| pnp_get_script_sample | Retrieves the full PnP PowerShell script code for a specific script sample by name, fetched live from GitHub. |
| pnp_suggest_script | Finds the most relevant community script samples for a task and returns their full script code plus adaptation guidance, in one call. |

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

## 🔗 Resources

- [PnP PowerShell documentation](https://pnp.github.io/powershell/)
- [PnP Script Samples](https://pnp.github.io/script-samples/)
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [MCP servers](https://github.com/modelcontextprotocol/servers?tab=readme-ov-file)
- [MCP inspector](https://github.com/modelcontextprotocol/inspector)
