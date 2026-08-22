<!-- markdownlint-disable MD024 MD012 -->
# PnP PowerShell MCP Server Changelog

*Please do not commit changes to this file, it is maintained by the repo owner.*

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/).


## [Current version]

### Added

### Changed

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

