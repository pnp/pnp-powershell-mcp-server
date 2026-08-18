using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>A long-lived <c>pwsh</c> child process that executes scripts one at a time.</summary>
// One process across calls is what lets a Connect-PnPOnline connection survive: PnP holds it in process
// memory, so the old process-per-call model dropped it and status always read "not connected".
// An in-proc runspace is not an option under native AOT, so this drives a child process over stdin.
internal sealed class PowerShellSession : IAsyncDisposable
{
    private const string PwshMissingMessage =
        "Error: Could not launch 'pwsh'. Install PowerShell 7+ from https://aka.ms/powershell and ensure it is available on PATH.";

    private const string ModuleMissingMessage =
        "Error: The PnP.PowerShell module is not installed. Install it by running: Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force";

    private const string SessionEndedMessage =
        "Error: The PowerShell session ended unexpectedly. Retry the command; the PnP connection will need to be re-established.";

    // Sentinels are per-session so that script output echoing a marker from another session
    // cannot terminate this session's read loop early.
    private readonly string _token = Guid.NewGuid().ToString("N");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stderrLock = new();
    private readonly Lock _processLock = new();
    private readonly StringBuilder _stderr = new();

    private Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private Process? _process;
    private StreamWriter? _stdin;

    public PowerShellSession(string id) => Id = id;

    public string Id { get; }

    public DateTimeOffset LastUsedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public bool IsAlive => _process is { HasExited: false };

    /// <summary>True while a command holds the session, keeping the idle evictor off work in progress.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    private string EndMarker => $"__PNP_END_{_token}__";

    private string ErrorMarker => $"__PNP_ERR_{_token}__";

    /// <summary>Runs a script in this session, starting the process on first use; calls are serialized.</summary>
    public async Task<string> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // The wait for the session is bounded by the same timeout as the command. Without this, a
        // quick metadata lookup queues behind a long-running command with no limit of its own.
        if (!await _gate.WaitAsync(timeout, cancellationToken))
        {
            return
                "Error: This session is busy running another command. Wait for it to finish, or end it with 'pnp_reset_session'. " +
                "To work in parallel, use a different sessionId.";
        }

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
            // Stamped on completion as well as on entry: a command that runs for longer than the idle
            // window would otherwise leave the session looking abandoned to the evictor.
            LastUsedUtc = DateTimeOffset.UtcNow;
            _gate.Release();
        }
    }

    /// <summary>Terminates the process; the next call starts a fresh one and discards the PnP connection.</summary>
    public async Task ResetAsync()
    {
        // Only a bounded wait for the gate. The usual reason to reset is a command that has wedged
        // while holding it, so blocking here would make recovery impossible exactly when it is needed;
        // terminating under a held gate is safe because the in-flight read fails and reports itself.
        var acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Terminate();
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
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
        // $__pnpScript is removed once the command finishes so the session does not hold on to the
        // last script text. Cleanup cannot prevent a collision with a user variable of the same name
        // — the assignment above already overwrote it — which is what the __pnp prefix is for.
        var wrapped =
            $"$__pnpScript = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{encoded}')); " +
            $"try {{ Invoke-Expression $__pnpScript }} catch {{ Write-Output '{ErrorMarker}'; Write-Output ($_ | Out-String) }}; " +
            $"Remove-Variable -Name __pnpScript -ErrorAction SilentlyContinue; " +
            $"Write-Output '{EndMarker}'";

        // Captured under the lock. ResetAsync and idle eviction can terminate without holding the
        // gate, so the writer may be torn down between the check and the write; reading the field
        // directly would surface that as a NullReferenceException instead of a clean message.
        StreamWriter? stdin;
        lock (_processLock)
        {
            stdin = _stdin;
        }

        if (stdin is null)
        {
            return SessionEndedMessage;
        }

        try
        {
            await stdin.WriteLineAsync(wrapped.AsMemory(), cancellationToken);
            await stdin.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Terminate();
            return SessionEndedMessage;
        }
        catch (OperationCanceledException)
        {
            // A partially written statement would be interpreted as a command of its own.
            Terminate();
            throw;
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
            return SessionEndedMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. The abandoned command keeps running and its output — including the
            // end marker — is still in flight, so leaving the session alive would hand that output to
            // whichever command runs next and desynchronize every read after it.
            Terminate();
            throw;
        }
        catch (OperationCanceledException)
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

        // Give the stderr pump a moment to catch up with output already flushed on stdout. Not
        // cancellable: the end marker has been consumed, so the stream is clean and abandoning the
        // result here would throw away a command that actually completed.
        await Task.Delay(25, CancellationToken.None);

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
        // Guarded because ResetAsync may terminate without holding the gate, concurrently with a
        // command that is mid-read.
        Process? process;
        lock (_processLock)
        {
            process = _process;
            _process = null;
            _stdin = null;
        }

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
        // Bounded for the same reason as ResetAsync: shutdown must not hang behind a wedged command.
        var acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Terminate();
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }

        // _gate is deliberately not disposed: an in-flight command would hit ObjectDisposedException on
        // Release. SemaphoreSlim only owns a resource once AvailableWaitHandle is touched, which it never is.
    }
}
