import { spawn, execSync } from "child_process";
import { readFile } from "fs/promises";
import { createRequire } from "module";
import { fileURLToPath } from "url";
import { dirname, join } from "path";
import Fuse from "fuse.js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);

export interface CmdletEntry {
  name: string;
  synopsis: string;
  docsPath: string;
}

interface CmdletError {
  error: string;
}

let _cmdletIndex: CmdletEntry[] | null = null;

function getCmdletIndex(): CmdletEntry[] {
  if (!_cmdletIndex) {
    _cmdletIndex = require("./cmdlet-index.json") as CmdletEntry[];
  }
  return _cmdletIndex;
}

export function searchCmdlets(
  query: string,
  limit: number = 10,
): CmdletEntry[] | CmdletError[] {
  try {
    const all = getCmdletIndex();

    const fuse = new Fuse(all, {
      keys: [
        { name: "name", weight: 0.8 },
        { name: "synopsis", weight: 0.2 },
      ],
      threshold: 0.4,
      includeScore: true,
      minMatchCharLength: 2,
    });

    const results = fuse.search(query);
    const capped = Math.min(limit, 50);
    return results.slice(0, capped).map((r) => r.item);
  } catch (err) {
    console.error("searchCmdlets error:", err);
    return [{ error: `Failed to search cmdlets: ${err}` }];
  }
}

const PNP_DOCS_BASE = "https://pnp.github.io/powershell/";

export async function getCmdletDocs(
  cmdletName: string,
  docsPath: string,
): Promise<string> {
  try {
    const url = `${PNP_DOCS_BASE}${docsPath}`;
    const response = await fetch(url);

    if (!response.ok) {
      if (response.status === 404) {
        return `Cmdlet '${cmdletName}' documentation not found at ${url}. Verify the cmdlet name is correct.`;
      }
      throw new Error(`HTTP ${response.status}: ${response.statusText}`);
    }

    const html = await response.text();
    return extractArticleText(html, cmdletName);
  } catch (err) {
    console.error("getCmdletDocs error:", err);
    return `Failed to retrieve documentation for '${cmdletName}': ${err}`;
  }
}

function extractArticleText(html: string, cmdletName: string): string {
  let content = html;
  const articleMatch = html.match(/<article[^>]*>([\s\S]*?)<\/article>/i);
  if (articleMatch) {
    content = articleMatch[1];
  }

  content = content.replace(/<script[\s\S]*?<\/script>/gi, "");
  content = content.replace(/<style[\s\S]*?<\/style>/gi, "");

  content = content.replace(
    /<\/?(h[1-6]|p|div|li|tr|th|td|br|hr)[^>]*>/gi,
    "\n",
  );

  content = content.replace(/<[^>]+>/g, "");
  content = content
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&nbsp;/g, " ");

  content = content.replace(/\n{3,}/g, "\n\n").trim();

  if (!content) {
    return `No documentation content found for '${cmdletName}'.`;
  }

  return content;
}

export function checkPwshAvailable(): void {
  try {
    execSync("pwsh --version", { stdio: "ignore" });
  } catch {
    throw new Error(
      "PowerShell 7+ (pwsh) is not available in PATH.\n" +
        "Install it from https://aka.ms/powershell before using pnpRunCmdlet.\n" +
        "On Windows: winget install Microsoft.PowerShell\n" +
        "On macOS:   brew install --cask powershell\n" +
        "On Linux:   see https://docs.microsoft.com/powershell/scripting/install/installing-powershell-on-linux",
    );
  }
}

const PWSH_TIMEOUT_MS = 120_000;

export async function runPwshCommand(expression: string): Promise<string> {
  const script = [
    '$ErrorActionPreference = "Stop"',
    '$WarningPreference = "SilentlyContinue"',
    '$VerbosePreference = "SilentlyContinue"',
    '$InformationPreference = "SilentlyContinue"',
    "Import-Module PnP.PowerShell -ErrorAction Stop",
    expression,
  ].join("; ");

  return new Promise<string>((resolve) => {
    let stdout = "";
    let stderr = "";
    let timedOut = false;

    const proc = spawn("pwsh", ["-NonInteractive", "-Command", script], {
      env: { ...process.env },
    });

    const timer = setTimeout(() => {
      timedOut = true;
      proc.kill();
    }, PWSH_TIMEOUT_MS);

    proc.stdout.on("data", (chunk: Buffer) => {
      stdout += chunk.toString("utf-8");
    });

    proc.stderr.on("data", (chunk: Buffer) => {
      stderr += chunk.toString("utf-8");
    });

    proc.on("close", (code) => {
      clearTimeout(timer);

      if (timedOut) {
        resolve(
          `[ERROR] Command timed out after ${PWSH_TIMEOUT_MS / 1000}s.\n` +
            "Consider breaking the operation into smaller steps or increasing the timeout.",
        );
        return;
      }

      if (code === 0) {
        resolve(stdout.trim() || "(No output returned)");
      } else {
        resolve(normalizeError(code, stdout.trim(), stderr.trim()));
      }
    });

    proc.on("error", (err) => {
      clearTimeout(timer);
      resolve(`[ERROR] Failed to spawn pwsh: ${err.message}`);
    });
  });
}

function normalizeError(
  code: number | null,
  stdout: string,
  stderr: string,
): string {
  const parts: string[] = [`[ERROR] PowerShell exited with code ${code}.`];

  if (stderr) {
    const fqeiMatch = stderr.match(/FullyQualifiedErrorId\s*:\s*(.+)/);
    if (fqeiMatch) {
      parts.push(`Error ID: ${fqeiMatch[1].trim()}`);
    }
    parts.push(`Details: ${stderr.trim()}`);
  }

  if (stdout) {
    parts.push(`Output before error: ${stdout.trim()}`);
  }

  return parts.join("\n");
}

export async function getBestPractices(): Promise<string> {
  try {
    const filePath = join(__dirname, "best-practices.md");
    return await readFile(filePath, "utf-8");
  } catch (err) {
    console.error("getBestPractices error:", err);
    return `Failed to load best practices document: ${err}`;
  }
}
