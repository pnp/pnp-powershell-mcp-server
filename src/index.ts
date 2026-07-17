#!/usr/bin/env node

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import * as util from "./util.js";
import { createRequire } from "module";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);

const pkg = require(join(__dirname, "..", "package.json")) as {
  version: string;
};

const server = new McpServer({
  name: "pnp-powershell-mcp-server",
  version: pkg.version,
});

server.registerTool(
  "pnpSearchCmdlets",
  {
    title: "Search PnP.PowerShell cmdlets",
    description:
      "Searches PnP.PowerShell cmdlets using fuzzy search based on a query string. " +
      "Use this tool first to discover relevant cmdlets before fetching full documentation.",
    inputSchema: {
      query: z
        .string()
        .describe(
          'Search query to find relevant cmdlets (e.g., "sharepoint list", "teams channel", "site permission")',
        ),
      limit: z
        .number()
        .optional()
        .describe("Maximum number of results to return (default: 10, max: 50)"),
    },
  },
  async ({ query, limit }) => {
    const maxLimit = Math.min(limit ?? 10, 50);
    const results = util.searchCmdlets(query, maxLimit);

    if (results.length === 0) {
      return {
        content: [
          { type: "text", text: `No cmdlets found matching "${query}".` },
          {
            type: "text",
            text: 'Try a broader query (e.g., just the noun part like "list", "site", "team").',
          },
        ],
      };
    }

    if ("error" in results[0]) {
      return {
        content: [{ type: "text", text: JSON.stringify(results) }],
      };
    }

    return {
      content: [
        {
          type: "text",
          text: `Found ${results.length} cmdlet(s) matching "${query}"`,
        },
        {
          type: "text",
          text: "TIP: Before executing a cmdlet, run pnpGetCmdletDocs to review its full documentation and correct parameter syntax.",
        },
        {
          type: "text",
          text: JSON.stringify(results),
        },
      ],
    };
  },
);

server.registerTool(
  "pnpGetCmdletDocs",
  {
    title: "Retrieve PnP.PowerShell cmdlet documentation",
    description:
      "Fetches full documentation for a specified PnP.PowerShell cmdlet including synopsis, syntax, parameters, and examples.",
    inputSchema: {
      cmdletName: z
        .string()
        .describe(
          'The cmdlet name to retrieve documentation for (e.g., "Get-PnPList")',
        ),
      docsPath: z
        .string()
        .describe(
          'The relative documentation path returned by pnpSearchCmdlets (e.g., "cmdlets/Get-PnPList.html")',
        ),
    },
  },
  async ({ cmdletName, docsPath }) => {
    const docs = await util.getCmdletDocs(cmdletName, docsPath);

    return {
      content: [
        {
          type: "text",
          text: "TIP: Use the cmdlet name and parameters exactly as documented. Pass the full expression to pnpRunCmdlet for execution.",
        },
        {
          type: "text",
          text: docs,
        },
      ],
    };
  },
);

server.registerTool(
  "pnpRunCmdlet",
  {
    title: "Execute a PnP.PowerShell expression",
    description:
      "Runs a PnP.PowerShell expression via a pwsh subprocess. " +
      "The module is imported automatically. " +
      "You must have called Connect-PnPOnline in a PowerShell session before running authenticated cmdlets. " +
      "Returns the cmdlet output or a descriptive error message.",
    inputSchema: {
      expression: z
        .string()
        .describe(
          'The PnP.PowerShell expression to execute (e.g., "Get-PnPList | Select-Object Title, Id | ConvertTo-Json")',
        ),
    },
  },
  async ({ expression }) => {
    try {
      util.checkPwshAvailable();
    } catch (err) {
      return {
        content: [{ type: "text", text: String(err) }],
      };
    }

    const result = await util.runPwshCommand(expression);
    return {
      content: [{ type: "text", text: result }],
    };
  },
);

server.registerTool(
  "pnpGetBestPractices",
  {
    title: "Retrieve PnP.PowerShell best practices",
    description:
      "Returns best-practice guidance for using PnP.PowerShell in AI-driven automation, " +
      "including authentication setup, connection patterns, error handling, output formatting, and security considerations.",
    inputSchema: {},
  },
  async () => {
    const content = await util.getBestPractices();
    return {
      content: [{ type: "text", text: content }],
    };
  },
);

const transport = new StdioServerTransport();
await server.connect(transport);
