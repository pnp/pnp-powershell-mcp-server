using PnPPowerShell.MCPServer.Services;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>A fixture must survive an edit to the script this server generates, or playback rots silently.</summary>
public class SessionTranscriptTests
{
    [Fact]
    public void A_labelled_fixture_keeps_its_key_when_the_generated_script_changes()
    {
        const string label = "run\nGet-PnPWeb | Select-Object Title";

        Assert.Equal(
            SessionTranscript.Key("$__pnpCommandResult = Invoke-Expression $x", label),
            SessionTranscript.Key("# a reworded wrapper\n$__pnpResult = & { $x }", label));
    }

    [Fact]
    public void Different_operations_on_the_same_command_are_different_fixtures()
    {
        Assert.NotEqual(
            SessionTranscript.Key("script", "run\nGet-PnPWeb"),
            SessionTranscript.Key("script", "analyse\nGet-PnPWeb"));
    }

    [Fact]
    public void Different_commands_are_different_fixtures()
    {
        Assert.NotEqual(
            SessionTranscript.Key("script", "run\nGet-PnPWeb"),
            SessionTranscript.Key("script", "run\nGet-PnPSite"));
    }

    [Fact]
    public void A_label_is_scrubbed_so_the_key_does_not_depend_on_the_recording_tenant()
    {
        Assert.Equal(
            SessionTranscript.Key("script", "run\nConnect-PnPOnline -Url 'https://acme.sharepoint.com/sites/x'"),
            SessionTranscript.Key("script", "run\nConnect-PnPOnline -Url 'https://contoso.sharepoint.com/sites/x'"));
    }

    [Fact]
    public void An_unlabelled_call_still_keys_on_the_script()
    {
        Assert.Equal(SessionTranscript.Key("some script"), SessionTranscript.Key("some script", null));
        Assert.NotEqual(SessionTranscript.Key("some script"), SessionTranscript.Key("another script"));
    }

    [Fact]
    public void Every_committed_fixture_names_the_operation_it_records()
    {
        var fixtures = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.transcript");

        Assert.NotEmpty(fixtures);
        Assert.All(fixtures, f =>
        {
            var header = File.ReadLines(f).Skip(1).First();
            Assert.StartsWith("# operation: ", header, StringComparison.Ordinal);
            Assert.DoesNotContain("unlabelled", header, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Every_committed_fixture_is_named_for_the_operation_it_records()
    {
        var fixtures = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.transcript");

        Assert.NotEmpty(fixtures);
        Assert.All(fixtures, f =>
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var key = File.ReadLines(f).First()["# key: ".Length..].Trim();

            // Lookup falls back to matching on the key, so a prefix that no longer describes the
            // operation is a readability bug rather than a broken fixture. Caught here either way.
            Assert.EndsWith($"-{key}", name, StringComparison.Ordinal);

            var prefix = name[..^(key.Length + 1)];
            Assert.NotEmpty(prefix);
            Assert.Matches("^[a-z0-9-]+$", prefix);
        });
    }

    [Fact]
    public void A_fixture_is_named_for_its_operation_and_found_by_its_key()
    {
        const string label = "run\nGet-PnPList | Select-Object Title";
        var directory = Path.Combine(Path.GetTempPath(), "pnp-transcript-" + Guid.NewGuid().ToString("n"));

        try
        {
            using (new EnvVar("PNP_MCP_RECORD_DIR", directory))
            {
                SessionTranscript.Record("$x = 1", "recorded output", label);
            }

            var written = Path.GetFileName(Assert.Single(Directory.GetFiles(directory)));
            Assert.Equal($"run-get-pnplist-select-object-title-{SessionTranscript.Key("$x = 1", label)}.transcript", written);

            using (new EnvVar("PNP_MCP_REPLAY_DIR", directory))
            {
                Assert.Equal("recorded output", SessionTranscript.Replay("$x = 1", label));

                // The readable half is cosmetic: renaming it must not orphan the fixture.
                File.Move(Path.Combine(directory, written), Path.Combine(directory, "renamed-by-hand-" + written));
                Assert.Equal("recorded output", SessionTranscript.Replay("$x = 1", label));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_long_operation_is_cut_at_a_word_boundary()
    {
        var name = SessionTranscript.FileName(
            "script",
            "run\nGet-PnPTenantSite -Identity 'https://contoso.sharepoint.com/sites/marketing' -Detailed");

        Assert.StartsWith("run-get-pnptenantsite-identity-https-contoso-", name, StringComparison.Ordinal);
        Assert.DoesNotContain("--", name, StringComparison.Ordinal);
    }
}
