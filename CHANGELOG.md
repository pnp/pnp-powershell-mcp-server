<!-- markdownlint-disable MD024 MD012 -->
# PnP PowerShell MCP Server Changelog

*Please do not commit changes to this file, it is maintained by the repo owner.*

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/).


## [Current version]

### Added

- Added `data/script-samples.json` and `data/pnp-commands.json` as embedded resources, generated from [pnp/vscode-pnp-powershell](https://github.com/pnp/vscode-pnp-powershell) by `build/Update-VendoredData.ps1`. `pnp_search_script_samples`, `pnp_get_script_sample` and `pnp_suggest_script` previously returned an error string unless an unrelated VS Code extension happened to be installed; they now answer offline. Both indexes record the source commit, which is printed with every answer, so a stale index is visible rather than silent.
- Added the `pnp_get_result_page` tool. When `pnp_run_command` produces a JSON result set larger than the output cap, it is now summarised — true row count, field names, and as many whole rows as fit — and the full set is held in the session that produced it, so "show me more" pages over rows already fetched instead of re-running the command against a live tenant.
- Added record-and-playback testing. `PNP_MCP_RECORD_DIR` records what a real session returned; `PNP_MCP_REPLAY_DIR` replays it with no `pwsh` and no tenant. `TranscriptScrubber` removes tenant hostnames, identities, GUIDs, tokens, secrets, thumbprints, certificate blocks and the account name in a profile path on the way in — including inside the base64 payload a command is wrapped in, and covering both the spaced and colon-bound forms of a secret parameter. Fixtures are filed under the operation they record — `run` plus the command, `command-docs` plus the cmdlet — so rewording the generated script does not silently orphan them, and they stay portable across tenants.
- Added `ToolSelectionEvaluator` and `e2eTestPrompts.md`, a BM25 scorer over the published tool descriptions that gates every prompt on ranking its tool in the top three. It needs no model, no network and no tenant. Confidence is a tools share of the top-3 shortlist rather than of all eleven tools, since dividing by the total made it zero-sum and measured how many tools the server has rather than how sure the choice is. The baseline is 56/56 top-3 and 89 % of prompts at or above the 0.4 confidence target.
- Added a markdown documentation link to `pnp_get_command_docs`, from the vendored cmdlet index. It is the source the HTML page is generated from, so it carries the same content for a fraction of the tokens, and it is present even when `pwsh` or the module is not.
- Added a vendored-index fallback to `pnp_search_commands`, so it still names cmdlets and their documentation on a machine where `pwsh` or `PnP.PowerShell` is missing.
- Added `StdioProtocolTests`, which drives the built server as a real process over newline-delimited JSON-RPC — initialize, tools/list, tools/call — with a hand-rolled client, so the published wire format is tested rather than the SDK talking to itself. It covers the tool surface, the annotations as published, and the destructive-command gate refusing a client that cannot be prompted.
- Added `modelSelections.md` and an agreement test, which compares the BM25 evaluators top pick against the tool a language model chose from the same published descriptions. They agree on 93 %; below 90 % the scorer, not the descriptions, is what needs replacing. The labels are not independent — the same model wrote the descriptions — so this checks that two mechanisms agree, not that the descriptions are good.
- Added `TranscriptScrubberFuzzTests`, which plants known identifiers in randomly assembled transcripts and asserts none survive. It found one: redacting a certificate injected real newlines, so any JSON output containing one became unparseable. The replacement is now a single line.
- Added a bare-runner CI job on ubuntu with no `pwsh` and no module, which is the only place the cold-start states can be manufactured for real and the only check on the offline claims — vendored indexes, recorded playback and the stdio protocol tests all have to pass there.
- Added tests for the scrubber, summarising and paging, and the vendored indexes.

### Changed

- Changed every tool description, driven by what the new tool-selection evaluator measured. `pnp_run_command` in particular described its mechanism rather than its job, and no task-shaped prompt selected it at all; it now names what it is for.
- Changed `ScriptSampleTools` to read from `ScriptSampleIndex`, which resolves the VS Code extension and `PNP_SCRIPT_SAMPLES_PATH` as overrides ahead of the vendored index rather than as the only sources.
- Changed the best practices guidance to cover summarise-and-paging and the markdown documentation link.

### Fixed

## [0.1.5-beta]

### Added

- Added the `pnp_diagnose_connection` tool and the supporting `ConnectionPreflight` service, which checks everything that has to be true before a command can run — `pwsh` on `PATH`, the `PnP.PowerShell` module and the connection held by the session — and names both the cause and the exact next command for every failing check. The `pwsh` and module checks need no tenant and no network, so it also works on a machine that is not set up yet. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added MCP resources through `PnPResources`, exposing the guidance and cmdlet help as `pnp://best-practices`, `pnp://best-practices/{section}` and `pnp://cmdlet/{name}`, so a client which supports resources can browse and cache the content instead of spending a tool call on it. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added tool annotations to the three remaining tools, so all ten now declare `readOnlyHint`, `idempotentHint` and `openWorldHint`, and the two which can change state also declare `destructiveHint`. A client can therefore decide what to auto-approve without guessing. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added error hints for the pre-connection case and for app registration and consent failures. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added an `output` section to the best practices resource and to the `section` parameter of `pnp_get_best_practices`. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added tests covering approval binding, connection preflight, resources, tool annotations and the new error hints. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added CHANGELOG.md for better maintainability. [#16](https://github.com/pnp/pnp-powershell-mcp-server/pull/16)

### Changed

- Changed the version to `0.1.5-beta` across the project file, the MCP server manifest and the documentation. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Changed the README and the best practices resource to document the new tool, the resources, the annotations and the confirmation gate. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)

### Fixed

- Fixed the destructive command confirmation bypass by removing `confirmDestructive` from the tool schema and binding an approval to an HMAC keyed fingerprint of the command, so the model can no longer approve its own destructive command. `PNP_MCP_CONFIRM_DESTRUCTIVE=false` is now the only way to bypass the gate. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Fixed the order in which error hints are matched, so the most specific hint wins. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)

### Contributors

- Gautam Sheth [gautamdsheth]
- Nishkalank Bezawada [NishkalankBezawada]

## [0.1.4-beta]

### Added

- Added read only mode, which blocks state changing PnP PowerShell cmdlets through a central `CommandPolicy` so the server can be pointed at a production tenant without being able to write to it. [#12](https://github.com/pnp/pnp-powershell-mcp-server/pull/12)
- Added a PowerShell AST based `ScriptAnalyzer` which resolves the cmdlets used in a script without executing it, making cmdlet lookups and policy decisions considerably faster. [#12](https://github.com/pnp/pnp-powershell-mcp-server/pull/12)
- Added `PnPErrorHints`, which enriches failed command output with an explanation of the most common PnP PowerShell errors and the likely way to resolve them. [#12](https://github.com/pnp/pnp-powershell-mcp-server/pull/12)
- Added an `OutputLimit` service which caps tool output at a configurable maximum number of characters and tells the client that the result was truncated, so a single command cannot exhaust the context window of the model. [#12](https://github.com/pnp/pnp-powershell-mcp-server/pull/12)
- Added guidance instructing the client to follow the `HelpUri` of a cmdlet to the PnP PowerShell documentation when the intended usage is unclear. [#12](https://github.com/pnp/pnp-powershell-mcp-server/pull/12)
- Added the `PnPPowerShell.MCPServer.Tests` project with coverage for command policy, output limits, error hints, script analysis, session handling and the best practices resource, and wired it into the CI workflow. [#12](https://github.com/pnp/pnp-powershell-mcp-server/pull/12)
- Added `CONTRIBUTING.md` and a pull request template. [#13](https://github.com/pnp/pnp-powershell-mcp-server/pull/13)

### Changed

- Changed the README and the best practices resource to document read only mode, the output size limit and their implications. [#12](https://github.com/pnp/pnp-powershell-mcp-server/pull/12), [#13](https://github.com/pnp/pnp-powershell-mcp-server/pull/13)

### Contributors

- Gautam Sheth [gautamdsheth]

## [0.1.3-beta]

### Changed

- Changed the release workflow, the package identity and the documented naming convention so that the published NuGet package and the release assets line up. [#8](https://github.com/pnp/pnp-powershell-mcp-server/pull/8)
- Changed the version to `0.1.3-beta` across the project file, the MCP server manifest and the documentation. [#10](https://github.com/pnp/pnp-powershell-mcp-server/pull/10)

### Contributors

- Nishkalank Bezawada [NishkalankBezawada]

## [0.1.2-beta]

### Added

- Added persistent PowerShell sessions through `PowerShellSession` and `PowerShellSessionManager`, so a `Connect-PnPOnline` now survives across tool calls instead of every call starting from an unauthenticated state. Sessions are addressed by a named `sessionId`, are evicted once idle and can be recycled with the new `pnp_reset_session` tool. [#6](https://github.com/pnp/pnp-powershell-mcp-server/pull/6)
- Added long running task support to `pnp_run_command`, so a command which outlives a single request keeps running and reports back on completion. [#6](https://github.com/pnp/pnp-powershell-mcp-server/pull/6)
- Added a confirmation prompt before running a destructive cmdlet, with a fallback for clients which cannot elicit a confirmation from the user. [#6](https://github.com/pnp/pnp-powershell-mcp-server/pull/6)
- Added tool annotations describing which tools are read only and which are destructive. [#6](https://github.com/pnp/pnp-powershell-mcp-server/pull/6)
- Added CI and release GitHub Actions workflows and a `RELEASING.md` describing the release process. [#6](https://github.com/pnp/pnp-powershell-mcp-server/pull/6)

### Changed

- Changed the MCP SDK from 1.2.0 to 2.2.0, added `ModelContextProtocol.Extensions.Tasks` and moved `Microsoft.Extensions.Hosting` from 8.0.1 to 10.0.11. [#6](https://github.com/pnp/pnp-powershell-mcp-server/pull/6)
- Changed the hard two minute kill of a running command into a configurable timeout defaulting to ten minutes, so a long running tenant operation is no longer terminated halfway through. [#6](https://github.com/pnp/pnp-powershell-mcp-server/pull/6)

### Contributors

- Gautam Sheth [gautamdsheth]

## [0.1.1-beta]

### Changed

- Changed the MCP server manifest and package metadata to match the version published to NuGet. [#4](https://github.com/pnp/pnp-powershell-mcp-server/pull/4)

### Contributors

- Nishkalank Bezawada [NishkalankBezawada]

## [0.1.0-beta]

### Added

- Initial release of the PnP PowerShell MCP Server, exposing PnP PowerShell to MCP clients as a native AOT, self contained .NET tool. [#2](https://github.com/pnp/pnp-powershell-mcp-server/pull/2)
- Added the `PnPPowerShellTools` tools for discovering and running PnP PowerShell cmdlets. [#2](https://github.com/pnp/pnp-powershell-mcp-server/pull/2)
- Added the `ScriptSampleTools` tools which surface PnP script samples to the client. [#2](https://github.com/pnp/pnp-powershell-mcp-server/pull/2)
- Added the embedded `best-practices.md` resource, which ships the authoring guidance with the server so it cannot drift from a second copy. [#2](https://github.com/pnp/pnp-powershell-mcp-server/pull/2)

### Contributors

- Nishkalank Bezawada [NishkalankBezawada]

