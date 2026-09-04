using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Records what a session returned; replays it with no pwsh and no tenant.</summary>
internal static partial class SessionTranscript
{
    /// <summary>Enough of the operation to recognise in a directory listing, not enough to be a path risk.</summary>
    private const int MaxSlugChars = 48;

    private const string UnlabelledSlug = "script";

    private const string ScriptMarker = "--- script ---";
    private const string CommandMarker = "--- command ---";
    private const string OutputMarker = "--- output ---";

    [GeneratedRegex(@"FromBase64String\('([A-Za-z0-9+/=]+)'\)")]
    private static partial Regex Base64PayloadRegex();

    public static string? RecordDirectory => Directory("PNP_MCP_RECORD_DIR");

    public static string? ReplayDirectory => Directory("PNP_MCP_REPLAY_DIR");

    public static bool IsReplaying
    {
        get
        {
            if (ReplayDirectory is not { } directory)
            {
                return false;
            }

            // Replay makes the server answer from files instead of the tenant, so say so once.
            if (!_announced)
            {
                _announced = true;
                Console.Error.WriteLine($"PNP_MCP_REPLAY_DIR is set: commands are answered from {directory}, not from Microsoft 365.");
            }

            return true;
        }
    }

    private static bool _announced;

    /// <summary>Identifies a fixture by its scrubbed script, so record and replay agree.</summary>
    public static string Key(string script, string? transcriptKey = null) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(
            transcriptKey is { Length: > 0 } label
                ? new TranscriptScrubber().Scrub(label)
                : ScrubScript(script, new TranscriptScrubber(), null)))))[..16];

    /// <summary>Names a fixture: a readable prefix, then the key that actually identifies it.</summary>
    public static string FileName(string script, string? transcriptKey = null) =>
        $"{Slug(transcriptKey)}-{Key(script, transcriptKey)}.transcript";

    /// <summary>The recorded output for this script, or an error naming the fixture that is missing.</summary>
    public static string Replay(string script, string? transcriptKey = null)
    {
        var key = Key(script, transcriptKey);
        var name = FileName(script, transcriptKey);
        var path = Path.Combine(ReplayDirectory!, name);

        if (!File.Exists(path))
        {
            // Only the key identifies a fixture, so renaming the readable half by hand cannot orphan it.
            var matches = System.IO.Directory.Exists(ReplayDirectory)
                ? System.IO.Directory.GetFiles(ReplayDirectory!, $"*{key}.transcript")
                : [];

            if (matches.Length != 1)
            {
                return
                    $"Error: No recorded transcript for this script (expected fixture {name}" +
                    (matches.Length > 1 ? $", and {matches.Length} files carry key {key}" : string.Empty) +
                    "). Playback covers only the operations that were recorded, and rewording the command " +
                    "changes the key. Re-record against a dev tenant with PNP_MCP_RECORD_DIR set, review the " +
                    "output, and commit the fixture.";
            }

            path = matches[0];
        }

        var content = File.ReadAllText(path);
        var start = content.IndexOf(OutputMarker, StringComparison.Ordinal);

        return start < 0
            ? $"Error: Fixture {Path.GetFileName(path)} has no '{OutputMarker}' section."
            : content[(start + OutputMarker.Length)..].TrimStart('\r', '\n').TrimEnd();
    }

    /// <summary>Writes a scrubbed fixture for this exchange. Overwrites, so re-recording is idempotent.</summary>
    public static void Record(string script, string output, string? transcriptKey = null)
    {
        var directory = RecordDirectory;
        if (directory is null)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(directory);

        var commands = new List<string>();
        var scrubber = new TranscriptScrubber();
        var scrubbedScript = ScrubScript(script, scrubber, commands);

        var fixture = new StringBuilder();
        fixture.AppendLine($"# key: {Key(script, transcriptKey)}");
        // Clamped: the label can be a whole command, and only Key needs it whole.
        fixture.AppendLine($"# operation: {(transcriptKey is { Length: > 0 } label ? OutputLimit.Echo(scrubber.Scrub(label).ReplaceLineEndings(" | ")) : "(unlabelled — keyed on the script itself)")}");
        fixture.AppendLine("# Scrubbed by TranscriptScrubber. Read it before committing: display names in free text are not detectable.");
        fixture.AppendLine();
        fixture.AppendLine(CommandMarker);
        fixture.AppendLine(commands.Count > 0 ? string.Join("\n", commands) : "(none — this script carries no encoded payload)");
        fixture.AppendLine();
        fixture.AppendLine(ScriptMarker);
        fixture.AppendLine(scrubbedScript);
        fixture.AppendLine();
        fixture.AppendLine(OutputMarker);
        fixture.AppendLine(scrubber.Scrub(output));

        File.WriteAllText(Path.Combine(directory, FileName(script, transcriptKey)), fixture.ToString());
    }

    /// <summary>The readable half of a filename. Cosmetic: the key beside it is the identity.</summary>
    // Scrubbed before slugging, so a tenant name cannot reach a filename.
    private static string Slug(string? transcriptKey)
    {
        if (transcriptKey is not { Length: > 0 } label)
        {
            return UnlabelledSlug;
        }

        var slug = new StringBuilder();

        foreach (var character in new TranscriptScrubber().Scrub(label))
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(char.ToLowerInvariant(character));
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        if (slug.ToString().Trim('-') is not { Length: > 0 } trimmed)
        {
            return UnlabelledSlug;
        }

        if (trimmed.Length <= MaxSlugChars)
        {
            return trimmed;
        }

        // Cut on a word boundary: a name ending mid-word reads like a corrupted file.
        var clamped = trimmed[..MaxSlugChars];
        var boundary = clamped.LastIndexOf('-');

        return boundary > 0 ? clamped[..boundary] : clamped;
    }

    /// <summary>Scrubs a script, including inside its base64 payload.</summary>
    private static string ScrubScript(string script, TranscriptScrubber scrubber, List<string>? commands) =>
        Base64PayloadRegex().Replace(scrubber.Scrub(script), match =>
        {
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
            }
            catch (FormatException)
            {
                return match.Value;
            }

            var clean = scrubber.Scrub(decoded);
            commands?.Add(clean);

            return $"FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(clean))}')";
        });

    private static string Normalize(string script) => script.Replace("\r\n", "\n").Trim();

    private static string? Directory(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
