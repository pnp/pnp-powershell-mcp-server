using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>A Git repository of samples, kept as a shallow copy under local app data.</summary>
internal static class SampleRepository
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    public static bool IsUrl(string entry) =>
        Uri.TryCreate(entry, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh";

    public static string Folder(string url) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pnp-powershell-mcp",
            "samples",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16]);

    /// <summary>Clones or updates the copy. When Git fails, the last good copy is used.</summary>
    public static string Sync(string url)
    {
        var folder = Folder(url);

        if (Directory.Exists(Path.Combine(folder, ".git")))
        {
            _ = Git("-C", folder, "fetch", "--depth", "1", "origin") && Git("-C", folder, "reset", "--hard", "FETCH_HEAD");
        }
        else
        {
            Git("clone", "--depth", "1", "--", url, folder);
        }

        return folder;
    }

    private static readonly Lazy<string?> SshCommand = new(() => BatchSsh(
        Environment.GetEnvironmentVariable("GIT_SSH_COMMAND"),
        Run(["config", "--get", "core.sshCommand"], sshCommand: null),
        Environment.GetEnvironmentVariable("GIT_SSH")));

    /// <summary>The ssh command Git would pick, in Git's order, with BatchMode so ssh fails rather than prompts.</summary>
    /// <returns>Null when Git would run the program GIT_SSH names, which takes no options.</returns>
    internal static string? BatchSsh(string? gitSshCommand, string? configured, string? gitSsh)
    {
        var command = new[] { gitSshCommand, configured }.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
        if (command is null && !string.IsNullOrWhiteSpace(gitSsh))
        {
            return null;
        }

        return $"{command ?? "ssh"} -o BatchMode=yes";
    }

    internal static bool Git(params string[] arguments) => Run(arguments, SshCommand.Value) is not null;

    /// <summary>Git's output when it succeeds, otherwise null.</summary>
    private static string? Run(string[] arguments, string? sshCommand)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" },
        };

        if (sshCommand is not null)
        {
            startInfo.Environment["GIT_SSH_COMMAND"] = sshCommand;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var git = Process.Start(startInfo)!;

            // stdin and stdout are the MCP channel, so the child must never inherit them.
            git.StandardInput.Close();
            var output = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();

            if (git.WaitForExit(GitTimeout))
            {
                return git.ExitCode != 0 ? null : output.Wait(GitTimeout) ? output.Result : string.Empty;
            }

            git.Kill(entireProcessTree: true);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
