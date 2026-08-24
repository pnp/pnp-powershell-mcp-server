# End-to-end tool-selection prompts

Every prompt here is a thing a SharePoint administrator might reasonably type, paired with the tool
that should answer it. `ToolSelectionEvaluatorTests` scores each one against the published tool
descriptions and fails the build when the right tool is not ranked in the top three.

**Adding a tool means adding prompts for it** — the test fails on any tool with none. When a prompt
regresses, the fix is almost always the tool's `[Description]`, not the prompt: the evaluator sees
exactly what an MCP client sees, so a prompt it cannot route is one a client may not route either.

## pnp_run_command

- Create a new communication site called Marketing at /sites/marketing
- Delete the archived project site collection
- Add three users to the Members group on the HR site
- Set the storage quota on the finance site to 5 gigabytes
- Upload every file in this folder to the Shared Documents library
- Show me the item count of each library on the intranet site

## pnp_search_commands

- What cmdlet do I use to work with retention labels
- Is there something for managing hub site associations
- Which cmdlets deal with Teams private channels
- Find the cmdlet name for adding a navigation node
- I need the cmdlet that handles term store groups

## pnp_get_command_docs

- What parameters does Set-PnPTenantSite accept
- Show me the syntax and examples for New-PnPSite
- Explain what the -Identity parameter means on Get-PnPListItem
- Give me the reference documentation for Add-PnPFile
- Which parameter sets does Connect-PnPOnline have

## pnp_get_result_page

- Show me the next page of those results
- Continue from row 250 of the result set you summarised
- I want to see the rest of the rows you were holding
- Page through the remainder of that large result set
- Give me rows 100 onwards from the previous output using the cursor

## pnp_get_connection_status

- Am I signed in right now
- Which site is this session currently connected to
- Check whether a connection already exists before we start
- What account is this session authenticated as
- Tell me the connection state of the reporting session

## pnp_diagnose_connection

- Nothing works on this machine and I do not know why
- Is pwsh installed and is the module available
- My very first call failed with an error I cannot explain
- Check that this machine is set up correctly for PnP
- Something is wrong with my environment, find out what

## pnp_reset_session

- Sign me out
- I need to switch to a different account
- The session has stopped responding, start it over
- Discard this session and its connection
- End the reporting session so it reconnects fresh

## pnp_get_best_practices

- What is the recommended way to work with this server
- Read me the guidance on handling large output
- What are the rules around destructive commands here
- Show me the authentication guidance for this server
- Explain the recommended workflow before I start

## pnp_search_script_samples

- Browse the community samples about document sets
- What community solutions exist for hub site reporting
- List the sample titles that mention permissions reports
- Show me which samples cover bulk Teams creation, titles only
- Are there any community samples about term store migration

## pnp_get_script_sample

- Get me the code for the sample named spo-create-documentset
- Fetch the full script for teams-bulk-create-teams
- Show the script body of that named sample
- Retrieve sample spo-export-list-items-to-csv in full
- Open the code of the sample you listed by its slug

## pnp_suggest_script

- Write me something to export every list item to a CSV file
- I need to automate creating Teams from a JSON file
- Help me build a script that reports on site collection permissions
- Give me a starting point for archiving inactive sites
- Automate removing orphaned users across the tenant
