# PnP.PowerShell MCP Server

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that exposes [PnP.PowerShell](https://pnp.github.io/powershell/) cmdlets to AI agents, enabling natural language management of Microsoft 365 via SharePoint Online, Microsoft Teams, Entra ID, Planner, Power Platform, and more.

## Description

This MCP server allows AI assistants to execute any PnP.PowerShell cmdlet using natural language. It handles complex prompts by chaining multiple PnP.PowerShell cmdlets to fulfil requests across Microsoft 365 workloads.

## Prerequisites

- **Node.js 20+**
- **PowerShell 7+** (`pwsh`) available in PATH
  - Windows: `winget install Microsoft.PowerShell`
  - macOS: `brew install --cask powershell`
  - Linux: [Install PowerShell](https://docs.microsoft.com/powershell/scripting/install/installing-powershell-on-linux)
- **PnP.PowerShell module** installed in PowerShell 7+:
  ```powershell
  Install-Module PnP.PowerShell -Scope CurrentUser
  ```
- **A registered Entra ID application** — see [Registering an Application](https://pnp.github.io/powershell/articles/registerapplication.html)

## Authentication Setup

The MCP server does not manage authentication. Before using `pnpRunCmdlet`, connect to your tenant:

```powershell
# Interactive
Connect-PnPOnline [yourtenant].sharepoint.com
    -ClientId <client id of your Entra ID Application Registration>
    -Interactive

# Certificate-based
Connect-PnPOnline [yourtenant].sharepoint.com
    -ClientId <client id of your Entra ID Application Registration>
    -Tenant <tenant>.onmicrosoft.com
    -CertificatePath <path to your .pfx certificate>
```

PnP.PowerShell caches the token to disk; subsequent `pnpRunCmdlet` calls reuse it automatically.

## How to Build and Run Locally

```bash
# Install dependencies
npm install

# Build TypeScript
npm run build

# Regenerate the cmdlet index (when PnP.PowerShell releases a new version)
npm run build:index

# Start the server
npm start

# Run with MCP Inspector (for debugging)
npm run inspect
```

## Tools

| Tool                  | Description                                                                                                                |
| --------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| `pnpSearchCmdlets`    | Fuzzy-searches the 890+ PnP.PowerShell cmdlet catalog by name or description. Use this first to discover the right cmdlet. |
| `pnpGetCmdletDocs`    | Fetches full documentation for a cmdlet (synopsis, syntax, parameters, examples) from the PnP.PowerShell docs site.        |
| `pnpRunCmdlet`        | Executes a PnP.PowerShell expression via a `pwsh` subprocess and returns the output.                                       |
| `pnpGetBestPractices` | Returns best-practice guidance for authentication, error handling, output formatting, and security.                        |

## Example Prompts

- _"Search for PnP.PowerShell cmdlets related to SharePoint lists"_
- _"Using PnP.PowerShell, get all lists from my SharePoint site at https://contoso.sharepoint.com/sites/demo and return the results as JSON"_
- _"Create a new Teams team called and add a welcome post to the General channel"_
- _"Get all Microsoft 365 groups that expire in the next 30 days"_

## Resources

- [PnP.PowerShell Documentation](https://pnp.github.io/powershell/)
- [MCP TypeScript SDK](https://github.com/modelcontextprotocol/typescript-sdk)
- [MCP Inspector](https://github.com/modelcontextprotocol/inspector)
