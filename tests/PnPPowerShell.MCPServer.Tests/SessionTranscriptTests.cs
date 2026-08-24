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
}
