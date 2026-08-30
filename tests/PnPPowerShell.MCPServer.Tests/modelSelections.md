# Model tool selection, for validating the evaluator

`ToolSelectionEvaluator` scores prompts with BM25 — pure lexical overlap. Nothing in the suite says
whether that has anything to do with how a model actually chooses a tool. This file is the check: each
prompt paired with the tool a language model picked when shown **only the eleven published
descriptions**, exactly as `tools/list` returns them, with no section headings and no other context.

`ToolSelectionEvaluatorTests` measures how often BM25's top-ranked tool matches the pick recorded here.
That number is the evaluator's own accuracy, and it is the thing to watch: if it falls, BM25 has stopped
predicting selection and the scorer needs replacing rather than the descriptions.

## What this is not

**These labels are not independent.** They come from the same model that wrote the descriptions and the
prompts, so agreement is inflated and this cannot show that the descriptions are good in the abstract.
What it can show is whether two *different mechanisms* — lexical overlap and semantic reading — pick the
same tool. Where they diverge is real information; where they agree, less so.

Re-labelling with a different model, or with a person, is the version of this file worth trusting. The
format is deliberately trivial so that is a drop-in replacement.

## Labels

Format: `- <tool> :: <prompt>`. A trailing `(ambiguous)` marks a prompt the labeller judged genuinely
open between two tools; those are excluded from the agreement figure and listed separately.

- pnp_run_command :: Create a new communication site called Marketing at /sites/marketing
- pnp_run_command :: Delete the archived project site collection
- pnp_run_command :: Add three users to the Members group on the HR site
- pnp_run_command :: Set the storage quota on the finance site to 5 gigabytes
- pnp_run_command :: Upload every file in this folder to the Shared Documents library
- pnp_run_command :: Show me the item count of each library on the intranet site
- pnp_search_commands :: What cmdlet do I use to work with retention labels
- pnp_search_commands :: Is there something for managing hub site associations
- pnp_search_commands :: Which cmdlets deal with Teams private channels
- pnp_search_commands :: Find the cmdlet name for adding a navigation node
- pnp_search_commands :: I need the cmdlet that handles term store groups
- pnp_get_command_docs :: What parameters does Set-PnPTenantSite accept
- pnp_get_command_docs :: Show me the syntax and examples for New-PnPSite
- pnp_get_command_docs :: Explain what the -Identity parameter means on Get-PnPListItem
- pnp_get_command_docs :: Give me the reference documentation for Add-PnPFile
- pnp_get_command_docs :: Which parameter sets does Connect-PnPOnline have
- pnp_get_result_page :: Show me the next page of those results
- pnp_get_result_page :: Continue from row 250 of the result set you summarised
- pnp_get_result_page :: I want to see the rest of the rows you were holding
- pnp_get_result_page :: Page through the remainder of that large result set
- pnp_get_result_page :: Give me rows 100 onwards from the previous output using the cursor
- pnp_get_connection_status :: Am I signed in right now
- pnp_get_connection_status :: Which site is this session currently connected to
- pnp_get_connection_status :: Check whether a connection already exists before we start
- pnp_get_connection_status :: What account is this session authenticated as
- pnp_get_connection_status :: Tell me the connection state of the reporting session
- pnp_diagnose_connection :: Nothing works on this machine and I do not know why
- pnp_diagnose_connection :: Is pwsh installed and is the module available
- pnp_diagnose_connection :: My very first call failed with an error I cannot explain
- pnp_diagnose_connection :: Check that this machine is set up correctly for PnP
- pnp_diagnose_connection :: Something is wrong with my environment, find out what
- pnp_reset_session :: Sign me out
- pnp_reset_session :: I need to switch to a different account
- pnp_reset_session :: The session has stopped responding, start it over
- pnp_reset_session :: Discard this session and its connection
- pnp_reset_session :: End the reporting session so it reconnects fresh
- pnp_get_best_practices :: What is the recommended way to work with this server
- pnp_get_best_practices :: Read me the guidance on handling large output
- pnp_get_best_practices :: What are the rules around destructive commands here
- pnp_get_best_practices :: Show me the authentication guidance for this server
- pnp_get_best_practices :: Explain the recommended workflow before I start
- pnp_search_script_samples :: Browse the community samples about document sets
- pnp_search_script_samples :: What community solutions exist for hub site reporting
- pnp_search_script_samples :: List the sample titles that mention permissions reports
- pnp_search_script_samples :: Show me which samples cover bulk Teams creation, titles only
- pnp_search_script_samples :: Are there any community samples about term store migration
- pnp_get_script_sample :: Get me the code for the sample named spo-create-documentset
- pnp_get_script_sample :: Fetch the full script for teams-bulk-create-teams
- pnp_get_script_sample :: Show the script body of that named sample
- pnp_get_script_sample :: Retrieve sample spo-export-list-items-to-csv in full
- pnp_get_script_sample :: Open the code of the sample you listed by its slug
- pnp_suggest_script :: Write me something to export every list item to a CSV file
- pnp_suggest_script :: I need to automate creating Teams from a JSON file
- pnp_suggest_script :: Help me build a script that reports on site collection permissions
- pnp_suggest_script :: Give me a starting point for archiving inactive sites
- pnp_suggest_script :: Automate removing orphaned users across the tenant (ambiguous)
- pnp_ping :: Lightweight health check to confirm the server is responsive
- pnp_ping :: What version and uptime does the server report
- pnp_ping :: Is the server responsive and what is its read-only mode status
- pnp_ping :: Show me the server version and active session count
- pnp_ping :: Run a health check and tell me the uptime
- pnp_list_sessions :: List all active PowerShell sessions with their status
- pnp_list_sessions :: What sessions exist before I decide which to connect or reuse
- pnp_list_sessions :: Show me each session and its last activity time
- pnp_list_sessions :: Which active sessions are running and what is their status
- pnp_list_sessions :: List sessions so I can decide which to reset or reuse
- pnp_setup_environment :: Install the PnP.PowerShell module for me
- pnp_setup_environment :: Install PnP PowerShell so I can run its cmdlets
- pnp_setup_environment :: Install the released build of the PnP.PowerShell module
- pnp_setup_environment :: Get the latest pre-release build of the PnP.PowerShell module installed
- pnp_setup_environment :: The PnP.PowerShell module is not installed, install it for me

## Ambiguous

- **Automate removing orphaned users across the tenant** — "automate" points at `pnp_suggest_script`,
  but an agent already connected would reasonably just do the work with `pnp_run_command`. Both are
  defensible from the descriptions alone, so this prompt cannot discriminate between them.
