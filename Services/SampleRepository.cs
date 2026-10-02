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

    internal static bool Git(params string[] arguments)
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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var git = Process.Start(startInfo)!;

            // stdin and stdout are the MCP channel, so the child must never inherit them.
            git.StandardInput.Close();
            _ = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();

            if (git.WaitForExit(GitTimeout))
            {
                return git.ExitCode == 0;
            }

            git.Kill(entireProcessTree: true);
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
