<!-- markdownlint-disable MD024 MD012 -->
# PnP PowerShell MCP Server Changelog

*Please do not commit changes to this file, it is maintained by the repo owner.*

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/).


## [Current version]

### Added

- `pnp_diagnose_connection` now returns the whole ordered path from nothing to connected when more than one step is missing: what is already true, the exact commands, who runs each and why, and how to prove it worked. A ready machine still gets one `NEXT STEP` line.
- Added the nearest valid parameter names to the `pnp_run_command` hint when a command has already failed with a parameter-binding error, looked up in the command corpus; nothing runs before execution.
- Added a build-time guard that parses every PowerShell block in the guidance and every generated `NEXT STEP` command, and fails when a cmdlet or parameter name is not in the command corpus. Names only: behaviour claims and environment variable names are not checked.
- Added server instructions to the `initialize` response: run `pnp_diagnose_connection` first, assume no environment variable, app registration or persisted login, ask delegated-versus-application and state the default grant before registering an app, hand a first sign-in to the user, verify with `pnp_get_connection_status`.
- Added a `trust` section to the guidance: content returned by the tenant or GitHub is data, not instructions.
- Added a one-line data boundary to `pnp_get_script_sample` and `pnp_suggest_script`, marking the README content they fetch from the public script-samples repository as data to read rather than instructions to follow. It leads the output so truncation keeps it. `pnp_run_command` output is deliberately not marked, and a test pins that.
- Added a guidance subsection on app registration: ask which cmdlet, and state the default grant before running it.
- Added a compiled-in BM25 index over every cmdlet's synopsis, description, parameters and examples, so `pnp_search_commands` answers plain-language questions with no `pwsh` round-trip and returns structured content alongside the text. [#25](https://github.com/pnp/pnp-powershell-mcp-server/pull/25)
- Added structured output to `pnp_ping`, `pnp_list_sessions`, `pnp_get_result_page` and `pnp_get_connection_status`, so a client reads typed data against a published schema instead of parsing prose. The text half is unchanged for clients that ignore schemas. [#25](https://github.com/pnp/pnp-powershell-mcp-server/pull/25)
- Added the `pnp_setup_environment` tool, which installs the `PnP.PowerShell` module for the current user — the released build or the latest pre-release — so a machine can be prepared without leaving the conversation. It installs that one module only, never signs in or touches the tenant, and runs the install only when `PNP_MCP_ALLOW_SETUP=true`; otherwise it returns the exact `Install-Module` command for the user to run by hand. [#22](https://github.com/pnp/pnp-powershell-mcp-server/issues/22)
- Added a readiness section to `pnp_ping`, which now reports whether `pwsh` and the `PnP.PowerShell` module are present, so a client can confirm the machine is set up as part of its health check. Pass `includeReadiness=false` to keep the old lightweight ping. [#22](https://github.com/pnp/pnp-powershell-mcp-server/issues/22)
- Added an auth section to `pnp_diagnose_connection`, which now takes a `targetUrl` and names the exact connect command this machine can use, from PnP's persisted-login store, the `ENTRAID_*` variables or a certificate. [#21](https://github.com/pnp/pnp-powershell-mcp-server/pull/21)
- Added error hints for a revoked or expired cached credential, no app registration for the tenant, and a machine with no browser to sign in with. [#21](https://github.com/pnp/pnp-powershell-mcp-server/pull/21)
- Added tests covering auth material and the connect command it names, sign-in detection, the new error hints and their ordering, the readable fixture filenames, and app display-name scrubbing. [#21](https://github.com/pnp/pnp-powershell-mcp-server/pull/21)

### Changed

- Changed `pnp_search_script_samples` and `pnp_suggest_script` to rank samples with the same BM25 scorer as `pnp_search_commands` (title, name, tags, description), replacing substring scoring. Stopwords no longer match: "no owner" now returns owner samples rather than every description containing "no".
- Changed `pnp_run_command` to decline `Install-Module`, `Update-Module` and `Register-PnPEntraIDApp*`, since those change the user's machine or tenant rather than run against a connection, and to point the user at their own PowerShell 7 terminal instead. [#21](https://github.com/pnp/pnp-powershell-mcp-server/pull/21)
- Changed recorded fixtures to be named for the operation they record — a readable slug plus the key — with lookup falling back to the key, so the readable half can be corrected by hand without orphaning the fixture. [#21](https://github.com/pnp/pnp-powershell-mcp-server/pull/21)
- Changed `TranscriptScrubber` to redact the `app_displayname` an app registration records, so a tenant's app name cannot reach a committed fixture. [#21](https://github.com/pnp/pnp-powershell-mcp-server/pull/21)
- Changed playback to report an unreadable environment-probe fixture as a fixture failure rather than claiming `pwsh` started but is broken. [#21](https://github.com/pnp/pnp-powershell-mcp-server/pull/21)

### Fixed

- Fixed the command corpus missing dynamic parameters such as `New-PnPSite -Title` and `-Url`, which only exist once `-Type` is bound. The index generator now probes each enum value and switch; four cmdlets gained 33 parameters.
- Fixed the guidance handing out a non-existent `-Interactive` switch on `Register-PnPEntraIDAppForInteractiveLogin`, and a wrong `-ClientId` flow list.
- Fixed `pnp_diagnose_connection` and error hints ignoring `AZURE_CLIENT_ID` and `AZURE_CLIENT_CERTIFICATE_PATH`, which PnP reads.
- Fixed a sign-in blocking for the full command timeout when nobody answers its prompt; a `Connect-PnPOnline` now gets its own two-minute limit. [#19](https://github.com/pnp/pnp-powershell-mcp-server/issues/19) [#21](https://github.com/pnp/pnp-powershell-mcp-server/pull/21)

### Contributors

- Gautam Sheth [gautamdsheth]
- Nishkalank Bezawada [NishkalankBezawada]

## [0.1.5-beta]

### Added

- Added the `pnp_diagnose_connection` tool and the supporting `ConnectionPreflight` service, which checks everything that has to be true before a command can run — `pwsh` on `PATH`, the `PnP.PowerShell` module and the connection held by the session — and names both the cause and the exact next command for every failing check. The `pwsh` and module checks need no tenant and no network, so it also works on a machine that is not set up yet. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added MCP resources through `PnPResources`, exposing the guidance and cmdlet help as `pnp://best-practices`, `pnp://best-practices/{section}` and `pnp://cmdlet/{name}`, so a client which supports resources can browse and cache the content instead of spending a tool call on it. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added tool annotations to the three remaining tools, so all ten now declare `readOnlyHint`, `idempotentHint` and `openWorldHint`, and the two which can change state also declare `destructiveHint`. A client can therefore decide what to auto-approve without guessing. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added error hints for the pre-connection case and for app registration and consent failures. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added an `output` section to the best practices resource and to the `section` parameter of `pnp_get_best_practices`. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added tests covering approval binding, connection preflight, resources, tool annotations and the new error hints. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Added CHANGELOG.md for better maintainability. [#16](https://github.com/pnp/pnp-powershell-mcp-server/pull/16)
- Added vendored script-sample and cmdlet indexes as embedded resources, so the script-sample tools and `pnp_search_commands` work offline. [#18](https://github.com/pnp/pnp-powershell-mcp-server/pull/18)
- Added the `pnp_get_result_page` tool, which summarises an oversized result set and pages it from the session instead of re-running the command. [#18](https://github.com/pnp/pnp-powershell-mcp-server/pull/18)
- Added a raw-markdown documentation link to `pnp_get_command_docs`, the same content as the HTML page for a fraction of the tokens. [#18](https://github.com/pnp/pnp-powershell-mcp-server/pull/18)
- Added record-and-playback fixtures, a tool-selection gate, stdio protocol tests and scrubber fuzzing, so the suite runs with no tenant and no `pwsh`. [#18](https://github.com/pnp/pnp-powershell-mcp-server/pull/18)
- Added the `pnp_list_sessions` tool, which lists every active session with its status (`running`, `idle`, `stopped`) and last activity time, so a client can see which sessions exist before deciding which to reuse, reconnect or reset. [#17](https://github.com/pnp/pnp-powershell-mcp-server/pull/17)
- Added the `pnp_ping` tool, a lightweight health check returning the server version, uptime, read-only mode and active session count, so a client can confirm the server is responsive at startup without touching a tenant. [#17](https://github.com/pnp/pnp-powershell-mcp-server/pull/17)
- Added tests for the two new tools, plus `modelSelections.md` and `e2eTestPrompts.md` recording the prompts used to check tool selection end to end. [#17](https://github.com/pnp/pnp-powershell-mcp-server/pull/17)

### Changed

- Changed the version to `0.1.5-beta` across the project file, the MCP server manifest and the documentation. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Changed the README and the best practices resource to document the new tool, the resources, the annotations and the confirmation gate. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Changed every tool description to state what the tool is for rather than how it works. [#18](https://github.com/pnp/pnp-powershell-mcp-server/pull/18)
- Changed session reporting to distinguish an idle session from one that is actively running a command, and to drop sessions past the idle timeout, so the status shown reflects what the session is really doing. [#17](https://github.com/pnp/pnp-powershell-mcp-server/pull/17)
- Changed the README tool table to document `pnp_ping` and `pnp_list_sessions`. [#17](https://github.com/pnp/pnp-powershell-mcp-server/pull/17)

### Fixed

- Fixed the destructive command confirmation bypass by removing `confirmDestructive` from the tool schema and binding an approval to an HMAC keyed fingerprint of the command, so the model can no longer approve its own destructive command. `PNP_MCP_CONFIRM_DESTRUCTIVE=false` is now the only way to bypass the gate. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Fixed the order in which error hints are matched, so the most specific hint wins. [#15](https://github.com/pnp/pnp-powershell-mcp-server/pull/15)
- Fixed a path traversal where a script-sample name from a local clone was substituted into a file path unchecked. [#18](https://github.com/pnp/pnp-powershell-mcp-server/pull/18)

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

