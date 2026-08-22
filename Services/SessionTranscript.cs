using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Records what a session returned; replays it with no pwsh and no tenant.</summary>
internal static partial class SessionTranscript
{
    private const string ScriptMarker = "--- script ---";
    private const string CommandMarker = "--- command ---";
    private const string OutputMarker = "--- output ---";

    [GeneratedRegex(@"FromBase64String\('([A-Za-z0-9+/=]+)'\)")]
    private static partial Regex Base64PayloadRegex();

    public static string? RecordDirectory => Directory("PNP_MCP_RECORD_DIR");

    public static string? ReplayDirectory => Directory("PNP_MCP_REPLAY_DIR");

    public static bool IsReplaying => ReplayDirectory is not null;

    /// <summary>Identifies a fixture by its scrubbed script, so record and replay agree.</summary>
    public static string Key(string script, string? transcriptKey = null) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(
            transcriptKey is { Length: > 0 } label
                ? new TranscriptScrubber().Scrub(label)
                : ScrubScript(script, new TranscriptScrubber(), null)))))[..16];

    /// <summary>The recorded output for this script, or an error naming the fixture that is missing.</summary>
    public static string Replay(string script, string? transcriptKey = null)
    {
        var key = Key(script, transcriptKey);
        var path = Path.Combine(ReplayDirectory!, key + ".transcript");

        if (!File.Exists(path))
        {
            return
                $"Error: No recorded transcript for this script (fixture {key}.transcript). Playback covers only the " +
                "scripts that were recorded, and any change to the script this server generates changes the key. " +
                "Re-record against a dev tenant with PNP_MCP_RECORD_DIR set, review the output, and commit the fixture.";
        }

        var content = File.ReadAllText(path);
        var start = content.IndexOf(OutputMarker, StringComparison.Ordinal);

        return start < 0
            ? $"Error: Fixture {key}.transcript has no '{OutputMarker}' section."
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
        fixture.AppendLine($"# operation: {(transcriptKey is { Length: > 0 } label ? scrubber.Scrub(label).ReplaceLineEndings(" | ") : "(unlabelled — keyed on the script itself, so editing that script orphans this fixture)")}");
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

        File.WriteAllText(Path.Combine(directory, Key(script, transcriptKey) + ".transcript"), fixture.ToString());
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
