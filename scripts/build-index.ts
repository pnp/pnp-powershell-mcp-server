#!/usr/bin/env node
/**
 * build-index.ts
 *
 * Fetches the PnP.PowerShell cmdlet listing page and builds a JSON index
 * with fields: name, synopsis, docsPath.
 *
 * Run via: node --loader ts-node/esm scripts/build-index.ts
 * Or after build: node scripts/build-index.js
 */

import { writeFile } from "fs/promises";
import { join, dirname } from "path";
import { fileURLToPath } from "url";

const __dirname = dirname(fileURLToPath(import.meta.url));

const CMDLETS_INDEX_URL = "https://pnp.github.io/powershell/cmdlets/";
const BASE_DOCS_URL = "cmdlets/";
const OUTPUT_PATH = join(__dirname, "..", "src", "cmdlet-index.json");

interface CmdletEntry {
  name: string;
  synopsis: string;
  docsPath: string;
}

/**
 * Derives a human-readable synopsis from a PnP.PowerShell cmdlet name.
 * E.g. "Get-PnPList" → "Gets SharePoint lists"
 *      "Set-PnPSite" → "Sets site properties"
 */
function deriveSynopsis(name: string): string {
  const match = name.match(/^([A-Z][a-z]+)-PnP(.+)$/);
  if (!match) return name;

  const verb = match[1];
  const noun = match[2];

  const verbMap: Record<string, string> = {
    Get: "Gets",
    Set: "Sets",
    Add: "Adds",
    Remove: "Removes",
    New: "Creates",
    Update: "Updates",
    Enable: "Enables",
    Disable: "Disables",
    Connect: "Connects",
    Disconnect: "Disconnects",
    Invoke: "Invokes",
    Export: "Exports",
    Import: "Imports",
    Install: "Installs",
    Publish: "Publishes",
    Uninstall: "Uninstalls",
    Register: "Registers",
    Unregister: "Unregisters",
    Move: "Moves",
    Copy: "Copies",
    Convert: "Converts",
    Rename: "Renames",
    Find: "Finds",
    Read: "Reads",
    Save: "Saves",
    Send: "Sends",
    Approve: "Approves",
    Deny: "Denies",
    Grant: "Grants",
    Revoke: "Revokes",
    Restore: "Restores",
    Repair: "Repairs",
    Reset: "Resets",
    Resolve: "Resolves",
    Request: "Requests",
    Restart: "Restarts",
    Receive: "Receives",
    Measure: "Measures",
    Merge: "Merges",
    Clear: "Clears",
    Submit: "Submits",
    Test: "Tests",
    Watch: "Watches",
    ConvertTo: "Converts to",
  };

  const verbDesc = verbMap[verb] ?? verb;

  const nounReadable = noun.replace(/([A-Z])/g, " $1").trim();

  return `${verbDesc} PnP ${nounReadable}`;
}

async function buildIndex(): Promise<void> {
  console.log(`Fetching cmdlet index from ${CMDLETS_INDEX_URL}...`);

  const response = await fetch(CMDLETS_INDEX_URL);
  if (!response.ok) {
    throw new Error(
      `Failed to fetch cmdlet index: ${response.status} ${response.statusText}`,
    );
  }

  const html = await response.text();

  const linkRegex = /href="([A-Z][a-zA-Z]+-PnP[a-zA-Z0-9]+)\.html"/g;
  const seen = new Set<string>();
  const cmdlets: CmdletEntry[] = [];

  let match: RegExpExecArray | null;
  while ((match = linkRegex.exec(html)) !== null) {
    const name = match[1];
    if (!seen.has(name)) {
      seen.add(name);
      cmdlets.push({
        name,
        synopsis: deriveSynopsis(name),
        docsPath: `${BASE_DOCS_URL}${name}.html`,
      });
    }
  }

  if (cmdlets.length === 0) {
    throw new Error(
      "No cmdlets found in the listing page. Check the URL or HTML structure.",
    );
  }

  cmdlets.sort((a, b) => a.name.localeCompare(b.name));

  await writeFile(OUTPUT_PATH, JSON.stringify(cmdlets, null, 2), "utf-8");
  console.log(`✓ Written ${cmdlets.length} cmdlet entries to ${OUTPUT_PATH}`);
}

buildIndex().catch((err) => {
  console.error("Failed to build cmdlet index:", err);
  process.exit(1);
});
