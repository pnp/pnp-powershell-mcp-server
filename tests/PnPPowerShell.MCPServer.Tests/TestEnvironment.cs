using System.Diagnostics;

// Several tests set process-wide environment variables, and the integration tests each drive a pwsh
// child process, so running them serially keeps both deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Detects once whether pwsh and the PnP.PowerShell module are present.</summary>
internal static class TestEnvironment
{
    private static readonly Lazy<bool> Available = new(Probe);

    public static bool PnPAvailable => Available.Value;

    private static bool Probe()
    {
        try
        {
            // stderr is not redirected: an unread redirected pipe can fill and deadlock the child.
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("if (Get-Module -ListAvailable -Name PnP.PowerShell) { 'YES' } else { 'NO' }");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Read asynchronously and bound the wait: ReadToEnd blocks until stdout closes, which a
            // hung pwsh never does, and that would stall the whole test run rather than skip a test.
            var output = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already gone; nothing to clean up.
                }

                return false;
            }

            return output.Wait(TimeSpan.FromSeconds(10))
                && output.Result.Contains("YES", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A fact that skips itself when pwsh or PnP.PowerShell is unavailable, as on a bare CI runner.</summary>
public sealed class RequiresPnPFactAttribute : FactAttribute
{
    public RequiresPnPFactAttribute()
    {
        if (!TestEnvironment.PnPAvailable)
        {
            Skip = "Requires pwsh with the PnP.PowerShell module installed.";
        }
    }
}

/// <summary>
/// The mirror of <see cref="RequiresPnPFactAttribute"/>: runs only where pwsh or PnP.PowerShell is
/// absent. The cold-start states cannot be manufactured on a developer machine that has both — pwsh
/// re-adds the default module paths, so PSModulePath cannot hide the module, and uninstalling it is
/// not something a test may do. A bare CI runner is that clean container, so the check runs there.
/// </summary>
public sealed class BarePnPFactAttribute : FactAttribute
{
    public BarePnPFactAttribute()
    {
        if (TestEnvironment.PnPAvailable)
        {
            Skip = "Runs only where pwsh or PnP.PowerShell is missing, which is the state it asserts.";
        }
    }
}

/// <summary>Sets an environment variable for the duration of a test and restores it afterwards.</summary>
internal sealed class EnvVar : IDisposable
{
    private readonly string _name;
    private readonly string? _original;

    public EnvVar(string name, string? value)
    {
        _name = name;
        _original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
}
