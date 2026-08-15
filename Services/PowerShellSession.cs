using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>
/// A long-lived <c>pwsh</c> child process that executes scripts one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Keeping a single process alive across tool calls is what allows a connection established by
/// <c>Connect-PnPOnline</c> to survive into later commands. PnP PowerShell holds its connection in
/// process memory, so the previous process-per-call model silently dropped it: every call started a
/// fresh runspace, which made <c>pnp_get_connection_status</c> report "not connected" no matter what
/// had been run before it.
/// </para>
/// <para>
/// An in-proc runspace via the PowerShell SDK is not an option here — this project publishes native
/// AOT and <c>System.Management.Automation</c> is heavily reflection-based — so the session is a
/// child process driven over stdin with sentinel-delimited output.
/// </para>
/// </remarks>
internal sealed class PowerShellSession : IAsyncDisposable
{
    private const string PwshMissingMessage =
        "Error: Could not launch 'pwsh'. Install PowerShell 7+ from https://aka.ms/powershell and ensure it is available on PATH.";

    private const string ModuleMissingMessage =
        "Error: The PnP.PowerShell module is not installed. Install it by running: Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force";

    // Sentinels are per-session so that script output echoing a marker from another session
    // cannot terminate this session's read loop early.
    private readonly string _token = Guid.NewGuid().ToString("N");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stderrLock = new();
    private readonly StringBuilder _stderr = new();

    private Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private Process? _process;
    private StreamWriter? _stdin;

    public PowerShellSession(string id) => Id = id;

    public string Id { get; }

    public DateTimeOffset LastUsedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public bool IsAlive => _process is { HasExited: false };

    private string EndMarker => $"__PNP_END_{_token}__";

    private string ErrorMarker => $"__PNP_ERR_{_token}__";

    /// <summary>
    /// Runs <paramref name="script"/> in this session, starting the underlying process on first use.
    /// Calls are serialized: a session is a single runspace and cannot interleave commands.
    /// </summary>
    public async Task<string> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LastUsedUtc = DateTimeOffset.UtcNow;

            var startError = await EnsureStartedAsync(cancellationToken);
            if (startError is not null)
            {
                return startError;
            }

            return await ExecuteCoreAsync(script, timeout, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Terminates the underlying process. The next <see cref="ExecuteAsync"/> starts a fresh one,
    /// which also discards any PnP connection held by this session.
    /// </summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Terminate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsAlive)
        {
            return null;
        }

        Terminate();
        _stdout = Channel.CreateUnbounded<string>();

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // "-Command -" makes pwsh read statements from stdin without emitting an interactive prompt.
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("-");

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException)
        {
            return PwshMissingMessage;
        }

        if (_process is null)
        {
            return PwshMissingMessage;
        }

        _stdin = _process.StandardInput;
        _ = PumpStdoutAsync(_process.StandardOutput, _stdout);
        _ = PumpStderrAsync(_process.StandardError);

        // Import the module once per session rather than once per command, which is both faster and
        // the reason a connection can persist at all.
        const string initScript = """
            $global:ErrorActionPreference = 'Stop'
            $global:ProgressPreference = 'SilentlyContinue'
            if (-not (Get-Module -ListAvailable -Name PnP.PowerShell)) {
              Write-Output '__PNP_MODULE_MISSING__'
            } else {
              Import-Module PnP.PowerShell -ErrorAction Stop
              Write-Output '__PNP_MODULE_READY__'
            }
            """;

        var init = await ExecuteCoreAsync(initScript, TimeSpan.FromMinutes(3), cancellationToken);

        if (init.Contains("__PNP_MODULE_MISSING__", StringComparison.Ordinal))
        {
            Terminate();
            return ModuleMissingMessage;
        }

        if (!init.Contains("__PNP_MODULE_READY__", StringComparison.Ordinal))
        {
            Terminate();
            return $"Error: Failed to initialize the PnP PowerShell session.\n{init}".TrimEnd();
        }

        return null;
    }

    private async Task<string> ExecuteCoreAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Discard anything left over from a previous command before issuing a new one.
        while (_stdout.Reader.TryRead(out _)) { }
        lock (_stderrLock)
        {
            _stderr.Clear();
        }

        // The payload is base64-encoded so that a multi-line or quote-heavy script cannot break the
        // one-statement-per-line stdin protocol or the surrounding try/catch.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var wrapped =
            $"$__pnpScript = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{encoded}')); " +
            $"try {{ Invoke-Expression $__pnpScript }} catch {{ Write-Output '{ErrorMarker}'; Write-Output ($_ | Out-String) }}; " +
            $"Write-Output '{EndMarker}'";

        try
        {
            await _stdin!.WriteLineAsync(wrapped.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            Terminate();
            return "Error: The PowerShell session ended unexpectedly. Retry the command; the PnP connection will need to be re-established.";
        }

        var output = new StringBuilder();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                var line = await _stdout.Reader.ReadAsync(timeoutCts.Token);
                if (string.Equals(line, EndMarker, StringComparison.Ordinal))
                {
                    break;
                }

                output.AppendLine(line);
            }
        }
        catch (ChannelClosedException)
        {
            Terminate();
            return "Error: The PowerShell session ended unexpectedly. Retry the command; the PnP connection will need to be re-established.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A pwsh process reading from a pipe cannot be interrupted the way Ctrl+C interrupts a
            // console, so the only way out of a runaway command is to end the session.
            Terminate();
            var limit = timeout.TotalMinutes >= 1
                ? $"{timeout.TotalMinutes:0.#} minute(s)"
                : $"{timeout.TotalSeconds:0} second(s)";

            return
                $"Error: The command exceeded {limit} and the PowerShell session was terminated. " +
                "Any PnP connection was lost and must be re-established with Connect-PnPOnline. " +
                "Consider narrowing the operation (-PageSize, Select-Object, a filtered query), or ask the client to run this tool as a task so it can run without a wall-clock limit.";
        }

        // Give the stderr pump a moment to catch up with output already flushed on stdout.
        await Task.Delay(25, cancellationToken);

        string errors;
        lock (_stderrLock)
        {
            errors = _stderr.ToString().Trim();
        }

        var text = output.ToString().Trim();

        var markerIndex = text.IndexOf(ErrorMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var before = text[..markerIndex].Trim();
            var failure = text[(markerIndex + ErrorMarker.Length)..].Trim();

            var message = $"Error: {failure}";
            if (before.Length > 0)
            {
                message = $"{message}\n\nOutput before the failure:\n{before}";
            }

            return message;
        }

        if (errors.Length > 0)
        {
            return text.Length == 0 ? errors : $"{text}\n\nWarnings:\n{errors}";
        }

        return text.Length == 0 ? "Command completed successfully (no output)." : text;
    }

    private static async Task PumpStdoutAsync(StreamReader reader, Channel<string> target)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await target.Writer.WriteAsync(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process ended; completing the channel surfaces that to any pending read.
        }
        finally
        {
            target.Writer.TryComplete();
        }
    }

    private async Task PumpStderrAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (_stderrLock)
                {
                    _stderr.AppendLine(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process ended; nothing further to collect.
        }
    }

    private void Terminate()
    {
        var process = _process;
        _process = null;
        _stdin = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // Already gone, or the platform refused the kill; disposing is all that is left.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Terminate();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
