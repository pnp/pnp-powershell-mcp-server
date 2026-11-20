using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>One JSON line per pnp_run_command call, in the file PNP_MCP_AUDIT_LOG names. Off when unset.</summary>
internal static class AuditLog
{
    internal const string PathVariable = "PNP_MCP_AUDIT_LOG";

    private const int MaxCommandChars = 4_000;

    /// <summary>Past this the file becomes "name.1", replacing the previous one.</summary>
    internal const long MaxFileBytes = 10_000_000;

    private static readonly Lock WriteLock = new();

    /// <summary>What a call fills in as it goes, so every exit is recorded with what was known by then.</summary>
    internal sealed class Entry
    {
        public string Session { get; set; } = PowerShellSessionManager.DefaultSessionId;

        public string Command { get; init; } = string.Empty;

        /// <summary>What the confirmation gate flagged; null when nothing needed confirming.</summary>
        public string? Flagged { get; set; }

        /// <summary>Null when no confirmation was asked for.</summary>
        public bool? Confirmed { get; set; }

        public bool Task { get; set; }

        /// <summary>Where the parse found secret values in <see cref="Command"/>; null when the command was never parsed.</summary>
        public IReadOnlyList<SecretSpan>? Secrets { get; set; }
    }

    // A relative path would resolve against whatever directory the client launched the server in.
    internal static string? FilePath =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } path && Path.IsPathFullyQualified(path.Trim())
            ? path.Trim()
            : null;

    internal static bool Enabled => FilePath is not null;

    /// <summary>The outcome a result text states.</summary>
    internal static string Classify(string result) =>
        !result.StartsWith("Error:", StringComparison.Ordinal) ? Refusal(result)
        : result.Contains(PowerShellSession.TerminatedMarker, StringComparison.Ordinal) ? "timeout"
        : result.StartsWith(Tools.PnPPowerShellTools.SignInTimedOut, StringComparison.Ordinal) ? "timeout"
        : "error";

    private static string Refusal(string result) =>
        result.StartsWith("Blocked:", StringComparison.Ordinal) ? "blocked"
        : result.StartsWith("Cancelled:", StringComparison.Ordinal) ? "cancelled"
        : "ok";

    internal static void Record(Entry entry, string outcome, TimeSpan duration)
    {
        if (FilePath is not { } path)
        {
            return;
        }

        try
        {
            var line = Format(entry, outcome, duration, DateTimeOffset.UtcNow);

            lock (WriteLock)
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                {
                    Directory.CreateDirectory(folder);
                }

                Write(path, line + "\n");
            }
        }
        catch (Exception ex)
        {
            // Every kind: this runs after the command, and a throw here would replace its result.
            Console.Error.WriteLine($"{PathVariable}: could not write to '{path}': {ex.Message}");
        }
    }

    /// <summary>Rotates and appends while holding "path.lock", so server processes sharing a log take turns.</summary>
    // Two appends opened at the same end-of-file overwrite each other; the in-process lock cannot see the other process.
    internal static void Write(string path, string text)
    {
        using var turn = TakeTurn(path + ".lock");

        Rotate(path);
        Append(path, text);
    }

    // Null after about two seconds: an entry written out of turn is better than one not written.
    private static FileStream? TakeTurn(string lockPath)
    {
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            try
            {
                return new FileStream(lockPath, OwnerOnly(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    // Commands can hold a secret no redaction recognises, and a usual umask would let other local users read the file.
    private static FileStreamOptions OwnerOnly(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    // A log that already existed keeps the mode it was created with, so it is narrowed here.
    private static void KeepOwnerOnly(string path)
    {
        const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & Others) != 0)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // On its own: a file something else holds open cannot be renamed, and the entry must still be written.
    private static void Rotate(string path)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > MaxFileBytes })
            {
                File.Move(path, path + ".1", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Shared for writing, and retried: a second server process may be appending to the same file.
    private static void Append(string path, string text)
    {
        // No byte-order mark: it would make the first line invalid JSON.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, OwnerOnly(FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
                KeepOwnerOnly(path);
                file.Write(bytes);

                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    internal static string Format(Entry entry, string outcome, TimeSpan duration, DateTimeOffset time)
    {
        // By position first: the parse sees what a pattern cannot. The patterns still run, for a command that was never parsed.
        var command = WellFormed(TranscriptScrubber.Secrets(Cut(entry.Command, entry.Secrets)));
        var kept = command.Length > MaxCommandChars ? WellFormed(command[..MaxCommandChars]) : command;
        var buffer = new ArrayBufferWriter<byte>();

        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("time", time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            json.WriteString("session", WellFormed(OutputLimit.Echo(entry.Session)));
            json.WriteString("command", kept);

            if (command.Length > MaxCommandChars)
            {
                json.WriteNumber("commandChars", command.Length);
            }

            json.WriteString("outcome", outcome);
            json.WriteNumber("durationMs", (long)duration.TotalMilliseconds);

            if (entry.Flagged is { } flagged)
            {
                json.WriteString("flagged", WellFormed(OutputLimit.Clamp(flagged) ?? string.Empty));
            }

            if (entry.Confirmed is { } confirmed)
            {
                json.WriteBoolean("confirmed", confirmed);
            }

            json.WriteBoolean("task", entry.Task);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Replaces each span with the redaction marker; spans that overlap or fall outside the text are tolerated.</summary>
    internal static string Cut(string command, IReadOnlyList<SecretSpan>? spans)
    {
        if (spans is not { Count: > 0 })
        {
            return command;
        }

        // Overlapping spans become one, so a value is replaced by one marker.
        var merged = new List<(int Start, int End)>();

        foreach (var span in spans.OrderBy(s => s.Start))
        {
            var start = Math.Clamp(span.Start, 0, command.Length);
            var end = Math.Clamp(span.End, 0, command.Length);

            if (start >= end)
            {
                continue;
            }

            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        var result = new StringBuilder(command);

        // Right to left, so an earlier offset is still true after a later one is replaced.
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            result.Remove(merged[i].Start, merged[i].End - merged[i].Start).Insert(merged[i].Start, "'[redacted-secret]'");
        }

        return result.ToString();
    }

    // A lone surrogate, typed or left by a cut, makes the JSON writer throw.
    private static string WellFormed(string text) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));

    /// <summary>Says once, on stderr, that a path was given and is not being used.</summary>
    internal static void WarnIfIgnored()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } given && FilePath is null)
        {
            Console.Error.WriteLine($"{PathVariable}: '{given}' is not a full path, so no audit log is being written.");
        }
    }
}
