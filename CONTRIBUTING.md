# Contribution guidance

Sharing is caring! All contributions to this repository are very welcome. This guidance should help you getting started contributing to the PnP PowerShell MCP Server by just following some easy steps.

There are various ways to accomplish the same goal. We'll go through a process here that should be easy to follow and accomplish for anyone. If you prefer using other tools over the ones mentioned here, such as using the cloning feature within Visual Studio, feel free to use that instead.

## Getting started

Follow the paragraphs below to get yourself started with contributing to this repository.

## Installing Git Tools

We'll be using the command line Git Tools to complete the steps. If you prefer using other tools, such as Visual Studio or the desktop client of Git, feel free to use that instead.

1. If you haven't got them already, install the Git Tools for your environment. They're available for Windows, Linux and Mac. Simply download the latest installer from:
   https://git-scm.com/downloads

1. There will be a lot of questions asked during the installer. Just use all defaults and next-next-finish through the installation process.

## Installing PowerShell 7

This project requires PowerShell 7 when you run and test the MCP server behavior that depends on pwsh and the PnP PowerShell module.

1. Navigate to the PowerShell 7 download page and download the latest version of PowerShell 7:
   https://learn.microsoft.com/powershell/scripting/install/installing-powershell

1. You can accept all the defaults and just do a next-next-finish installation.

## Installing the .NET SDK 10

To be able to compile this repository, you need to have the .NET SDK 10 installed. If you don't have it installed yet, follow the steps below.

1. Navigate to the .NET download page and download the latest .NET 10 SDK:
   https://dotnet.microsoft.com/download

1. You can accept all the defaults and just do a next-next-finish installation.

## Create your own Fork

To contribute to a GitHub project, what you do first is create a fork. Basically it means you will get your own copy of the source code. To do so, follow the steps below.

1. Go to the repository on GitHub:
   https://github.com/pnp/pnp-powershell-mcp-server

1. Make sure you're logged on to GitHub. If you don't have a GitHub account yet, create one and log on first before you continue.

1. Click the Fork button in the top right corner of the page.

1. In the fork creation options, uncheck **Copy the main branch only**.

## Updating your Fork

Now that you have your own fork, you need to make sure it's up to date with the latest changes from the main repository. Do this every time before you start working on a change. If you don't do so, it will become much harder for us to review and merge your changes.

Important: this project accepts pull requests against the dev branch only.

1. First identify if your forked dev branch is already up to date.

1. If it is behind, click Sync fork and then Update branch in GitHub.

1. If you prefer command line, run:

   ```powershell
   git fetch upstream
   git checkout dev
   git merge upstream/dev
   git push origin dev
   ```

## Cloning the repository to your local file system

The next step is to download, or clone, your fork of the repository to your local machine so you can work on updating it.

1. Open a command prompt or PowerShell window and navigate to the folder where you want to clone the repository to. For example, if you want to clone it to your C:\Source folder, you would do the following:

   ```powershell
   cd C:\Source
   ```

1. Look up the URL of your fork. You can find it by clicking on the Code button on your forked repository on GitHub.

1. In the command prompt or PowerShell window, type the following command and replace the URL with the URL of your fork:

   ```powershell
   git clone <URL of repository>
   ```

1. Add a reference to the upstream repository. This will allow you to pull in changes from the main repository to your local copy:

   ```powershell
   git remote add upstream https://github.com/pnp/pnp-powershell-mcp-server.git
   ```

1. Validate if the upstream has been added successfully by executing:

   ```powershell
   git remote -v
   ```

1. Ensure you have a local `dev` branch that tracks your fork's `dev` branch:

   ```powershell
   git fetch origin
   git checkout -b dev origin/dev
   ```

   If you already have a local `dev` branch, use:

   ```powershell
   git checkout dev
   git pull origin dev
   ```

## Making changes to the code

You are now ready to start making changes to the code.

1. Open Visual Studio Code and use File > Open Folder.

1. Select the folder you cloned for this repository.

1. If a dialog pops up asking if you trust the authors of the files in the folder, click Yes, I trust the authors.

1. Before starting to make changes, create a new branch for your changes from dev.

   ```powershell
   git checkout dev
   git pull origin dev
   git checkout -b <your-branch-name>
   ```

1. Double-check that your feature branch is based on `dev` and not `main`.

1. Use a distinctive branch name that makes it easy to identify the change.

Some hints on how to work with Visual Studio Code more easily:

- Use CTRL+P to search for existing files quickly.
- Please only submit one type of change per pull request. If you want to submit multiple changes, please submit them as separate pull requests.

## Testing your changes

If you have only updated documentation files, there is usually no need to run deeper validation. Read through your changes once more to ensure there are no typos.

If you have updated code, you need to test your changes to make sure they work as expected.

1. Build from the repository root:

   ```powershell
   dotnet build
   ```

1. Run the MCP server from source:

   ```powershell
   dotnet run --project ./PnPPowerShell.MCPServer.csproj
   ```

1. Optionally use MCP Inspector to validate tools locally:

   ```powershell
   npx @modelcontextprotocol/inspector dotnet run --project ./PnPPowerShell.MCPServer.csproj
   ```

1. Run the test suite. It needs no tenant and no network — the tenant-dependent tests replay recorded
   fixtures, and the tests that need `pwsh` and the `PnP.PowerShell` module skip themselves when either
   is absent:

   ```powershell
   dotnet test
   ```

1. If you are making packaging changes, verify your publish flow on at least one RID:

   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained
   ```

### If you added or changed a tool

- **Add prompts for it** to [e2eTestPrompts.md](./tests/PnPPowerShell.MCPServer.Tests/e2eTestPrompts.md).
  `ToolSelectionEvaluatorTests` fails on any tool with none, and gates every prompt on ranking its tool
  in the top three against the published descriptions. When a prompt regresses, fix the tool's
  `[Description]` rather than the prompt: the evaluator reads exactly what an MCP client reads.
- **Declare its annotations.** `readOnlyHint`, `idempotentHint` and `openWorldHint` are required, plus
  `destructiveHint` for anything that can change state. A test enforces this.

### If you changed the script generated for a session

Recorded fixtures are keyed on that script, so changing it invalidates them and playback will say which
fixture is missing. Re-record from a machine with a connected dev tenant, then **read every fixture
before committing it** — the scrubber cannot detect a display name in free text. See
[Recorded-playback tests](./README.md#recorded-playback-tests).

## Submitting your changes for review

Once you're done making and testing your changes, you need to submit them for review in a Pull Request, or PR in short.

1. Within Visual Studio Code, go to Source Control, review your changes and commit them with a meaningful commit message.

1. Push your branch to GitHub.

1. Open your browser and go to:
   https://github.com/pnp/pnp-powershell-mcp-server

1. Click Compare and pull request.

1. Important: set the base branch to dev. Pull requests must target dev only.

1. Provide a meaningful title and a description that explains what you changed and why.

1. Keep Allow edits from maintainers enabled.

Thanks for contributing!

## Troubleshooting

### My local fork is ahead of pnp:dev

1. First proceed with the steps in the Cloning the repository to your local file system section to make sure you have a local copy of your version of the code.

1. In a command prompt or PowerShell window, navigate to the folder where you cloned the repository to and execute:

   ```powershell
   git fetch upstream
   ```

1. Execute the following command to reset your local dev branch to the upstream dev branch:

   ```powershell
   git checkout dev
   git reset --hard upstream/dev
   git push origin dev --force
   ```

### Visual Studio Code shows a dialog mentioning Make sure you configure your user.name and user.email in git

If Visual Studio Code shows this dialog, click Cancel and open a PowerShell window and execute the following commands, replacing the values with your information:

```powershell
git config --global user.name "John Doe"
git config --global user.email "johndoe@outlook.com"
```

You only need to do this once on your machine.

### Build or publish fails for Native AOT

Native AOT requires platform-specific toolchains:

- Windows: Visual Studio Desktop development with C++ workload
- macOS: Xcode command line tools
- Linux: clang and zlib1g-dev

For complete release instructions, see RELEASING.md.

## Additional links

- Code: https://github.com/pnp/pnp-powershell-mcp-server
- Issues: https://github.com/pnp/pnp-powershell-mcp-server/issues
- Pull requests: https://github.com/pnp/pnp-powershell-mcp-server/pulls
