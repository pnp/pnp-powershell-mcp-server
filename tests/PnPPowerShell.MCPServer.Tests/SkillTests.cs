#pragma warning disable MCPEXP005 // Skills extension is experimental in SDK 2.4.0.
using ModelContextProtocol.Extensions.Skills;
using PnPPowerShell.MCPServer.Tools;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Tests;

public sealed class SkillTests : IDisposable
{
    private const string ConnectUri = "skill://pnp-connect/SKILL.md";

    private readonly McpStdioClient _client = new();

    public void Dispose() => _client.Dispose();

    [Fact]
    public void The_connect_skill_is_a_valid_entry_named_for_its_directory()
    {
        var skill = PnPSkills.Connect.Value.Skill.ProtocolSkill!;

        Assert.Equal(ConnectUri, skill.Uri);
        Assert.Equal(PnPSkills.ConnectName, skill.Name);
        Assert.Equal(PnPSkills.ConnectDescription, skill.Description);
        Assert.InRange(PnPSkills.ConnectDescription.Length, 1, 1024);
    }

    [Fact]
    public void The_manifest_is_computed_from_the_bytes_that_are_served()
    {
        var bytes = Encoding.UTF8.GetBytes(PnPSkills.ConnectMarkdown());
        var entry = Assert.Single(PnPSkills.Connect.Value.Skill.ProtocolSkill!.Resources.Entries!);

        Assert.Equal(ConnectUri, entry.Uri);
        Assert.Equal(bytes.Length, entry.Size);
        Assert.Equal(SkillVerifier.ComputeDigest(bytes), entry.Digest);
    }

    /// <summary>The skill is a cut of the guidance, not a second copy that can drift.</summary>
    [Fact]
    public void The_skill_carries_its_guidance_sections_verbatim()
    {
        var document = PnPPowerShellTools.BestPracticesDocument.Value;
        var markdown = PnPSkills.ConnectMarkdown();

        foreach (var heading in PnPSkills.ConnectSections)
        {
            var section = PnPPowerShellTools.ExtractSections(document, [heading]).Replace("\r\n", "\n").TrimEnd();

            Assert.StartsWith("## " + heading, section, StringComparison.Ordinal);
            Assert.Contains(section, markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_skill_is_small_and_has_the_same_bytes_on_every_platform()
    {
        var markdown = PnPSkills.ConnectMarkdown();

        Assert.DoesNotContain('\r', markdown);
        Assert.EndsWith("\n", markdown, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(markdown), 1, 100 * 1024);
    }

    /// <summary>PNP_MCP_TOOLS=core must not leave the skill pointing at a tool that is not published.</summary>
    [Fact]
    public void The_skill_names_only_core_tools()
    {
        var named = Regex.Matches(PnPSkills.ConnectMarkdown(), @"\bpnp_[a-z_]+\b").Select(m => m.Value).Distinct().ToList();

        Assert.NotEmpty(named);
        Assert.All(named, name => Assert.Contains(name, ToolOrder.Core));
    }

    /// <summary>The status reads the held connection object, so it cannot settle a failed sign-in or an account switch.</summary>
    [Fact]
    public void A_held_connection_does_not_end_a_recovery_or_an_account_switch()
    {
        var steps = PnPSkills.ConnectMarkdown().Split("\n## ")[0];

        Assert.Contains("not whether those credentials", steps, StringComparison.Ordinal);
        Assert.Contains("only for an ordinary request to connect", steps, StringComparison.Ordinal);
        Assert.Contains("another account or one the status does not confirm", steps, StringComparison.Ordinal);
        Assert.Contains("a sign-in or permission failure", steps, StringComparison.Ordinal);
    }

    [Fact]
    public void The_server_declares_the_skills_extension()
    {
        var extensions = _client.Discover().GetProperty("capabilities").GetProperty("extensions");

        Assert.True(extensions.TryGetProperty(SkillsProtocol.ExtensionId, out _), "The server does not declare the Skills extension.");
    }

    /// <summary>What a host does before loading: list, read, then check size, digest and frontmatter.</summary>
    [Fact]
    public void A_listed_skill_reads_back_as_the_manifest_describes()
    {
        _client.Discover();

        var listed = Assert.Single(_client.Request(SkillsProtocol.MethodSkillsList, Meta(new JsonObject())).GetProperty("skills").EnumerateArray());
        var manifest = Assert.Single(listed.GetProperty("resources").EnumerateArray());

        var text = _client.Request("resources/read", Meta(new JsonObject { ["uri"] = ConnectUri }))
            .GetProperty("contents")[0].GetProperty("text").GetString()!;
        var bytes = Encoding.UTF8.GetBytes(text);

        Assert.Equal(ConnectUri, listed.GetProperty("uri").GetString());
        Assert.Equal(PnPSkills.ConnectName, listed.GetProperty("frontmatter").GetProperty("name").GetString());
        Assert.Equal(bytes.Length, manifest.GetProperty("size").GetInt64());
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), manifest.GetProperty("digest").GetString());
        Assert.StartsWith($"---\nname: {PnPSkills.ConnectName}\ndescription: {PnPSkills.ConnectDescription}\n---\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_skill_is_refused_as_invalid_params()
    {
        _client.Discover();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            _client.Request(SkillsProtocol.MethodSkillsGet, Meta(new JsonObject { ["uri"] = "skill://no-such-skill/SKILL.md" })));

        Assert.Contains("-32602", failure.Message, StringComparison.Ordinal);
    }

    private static string Meta(JsonObject parameters) => McpStdioClient.WithMeta(parameters, tasks: false);
}
