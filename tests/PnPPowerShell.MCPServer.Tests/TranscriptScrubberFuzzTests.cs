using PnPPowerShell.MCPServer.Services;
using System.Text;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Generated transcripts, checked for anything identifying that survived the scrubber.</summary>
// The hand-written tests only cover the shapes someone thought of. These plant known markers in
// randomly assembled output and assert none of them come out the other side, which is the property
// that actually matters: a fixture is committed to a public repository.
//
// Deterministic by seed, so a failure names the seed that produced it and can be replayed.
public class TranscriptScrubberFuzzTests
{
    private const int Cases = 400;

    /// <summary>Markers are distinctive, so finding one in the output is proof rather than coincidence.</summary>
    private static readonly string[] Secrets =
    [
        "zqxsecret", "zqxtenant", "zqxperson", "zqxaccount", "zqxthumb", "zqxtoken", "zqxcert",
    ];

    [Fact]
    public void No_planted_identifier_survives()
    {
        var failures = new List<string>();

        for (var seed = 0; seed < Cases; seed++)
        {
            var scrubbed = new TranscriptScrubber().Scrub(Generate(seed, out var planted));

            foreach (var marker in planted.Where(p => scrubbed.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"seed {seed}: '{marker}' survived");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(15)));
    }

    [Fact]
    public void Scrubbing_is_stable_for_the_same_input()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var transcript = Generate(seed, out _);

            Assert.Equal(new TranscriptScrubber().Scrub(transcript), new TranscriptScrubber().Scrub(transcript));
        }
    }

    /// <summary>A fixture is only replayable if scrubbing left the payload parseable.</summary>
    [Fact]
    public void Json_output_is_still_json_afterwards()
    {
        var failures = new List<string>();

        for (var seed = 0; seed < Cases; seed++)
        {
            var json = GenerateJson(seed);
            var scrubbed = new TranscriptScrubber().Scrub(json);

            try
            {
                using var _ = JsonDocument.Parse(scrubbed);
            }
            catch (JsonException ex)
            {
                failures.Add($"seed {seed}: {ex.Message}\n  in: {json}\n  out: {scrubbed}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(3)));
    }

    /// <summary>Builds a transcript out of randomly chosen fragments, and reports what it planted.</summary>
    private static string Generate(int seed, out List<string> planted)
    {
        var random = new Random(seed);
        var markers = new List<string>();
        var text = new StringBuilder();

        for (var i = 0; i < random.Next(1, 6); i++)
        {
            text.AppendLine(Fragment(random, markers));
        }

        planted = markers;
        return text.ToString();
    }

    private static string Fragment(Random random, List<string> planted)
    {
        var id = random.Next(1000, 9999);

        switch (random.Next(10))
        {
            case 0:
                planted.Add($"zqxtenant{id}");
                return $"https://zqxtenant{id}.sharepoint.com/sites/project";

            case 1:
                planted.Add($"zqxtenant{id}");
                return $"Connect-PnPOnline -Url 'https://zqxtenant{id}-admin.sharepoint.com' -Interactive";

            case 2:
                planted.Add($"zqxperson{id}");
                return $$"""{"Owner":"i:0#.f|membership|zqxperson{{id}}@zqxtenant{{id}}.onmicrosoft.com","Id":{{id}}}""";

            case 3:
                planted.Add($"zqxsecret{id}");
                // Both binding forms, since only one of them used to be covered.
                return random.Next(2) == 0
                    ? $"Connect-PnPOnline -ClientSecret 'zqxsecret{id}xyz'"
                    : $"Connect-PnPOnline -ClientSecret:'zqxsecret{id}xyz'";

            case 4:
                planted.Add($"zqxaccount{id}");
                return random.Next(2) == 0
                    ? $@"Export-Csv -Path 'C:\Users\zqxaccount{id}\report.csv'"
                    : $"Export-Csv -Path '/home/zqxaccount{id}/report.csv'";

            case 5:
                planted.Add($"zqxtoken{id}");
                return $"Authorization: Bearer zqxtoken{id}AAAABBBBCCCCDDDDEEEEFFFF0123456789";

            case 6:
                // A real GUID carries no marker, so the check is that it is replaced by a placeholder one.
                var guid = $"{id:x4}0000-0000-4000-8000-{id:d12}";
                planted.Add(guid);
                return $$"""{"SiteId":"{{guid}}","Template":"STS#3"}""";

            case 7:
                planted.Add($"zqxthumb{id}");
                return $"-Thumbprint {Hex40($"zqxthumb{id}")}";

            case 8:
                planted.Add($"zqxperson{id}");
                return $"Error: user zqxperson{id}@contoso.com does not have permission to view this list.";

            default:
                planted.Add($"zqxcert{id}");
                return $"-----BEGIN CERTIFICATE-----\nzqxcert{id}MIIEvQIBADANBgkqhkiG9w0B\n-----END CERTIFICATE-----";
        }
    }

    /// <summary>Generates a JSON document shaped like PnP output, with identifying values inside it.</summary>
    private static string GenerateJson(int seed)
    {
        var random = new Random(seed);
        var rows = new List<string>();

        for (var i = 0; i < random.Next(1, 4); i++)
        {
            var id = random.Next(1000, 9999);
            rows.Add($$"""
                {"Url":"https://zqxtenant{{id}}.sharepoint.com/sites/s{{id}}","Owner":"zqxperson{{id}}@zqxtenant{{id}}.onmicrosoft.com",
                 "GroupId":"00000000-0000-0000-0000-000000000000","SiteId":"{{id:x4}}0000-0000-4000-8000-{{id:d12}}",
                 "Title":"Zqxtenant{{id}} Project","Cert":"-----BEGIN CERTIFICATE-----\nMIIEvQIBADANBgkq\n-----END CERTIFICATE-----"}
                """);
        }

        return "[" + string.Join(",", rows) + "]";
    }

    /// <summary>A deterministic 40-hex string, the shape a certificate thumbprint has.</summary>
    private static string Hex40(string source)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(source));

        return Convert.ToHexStringLower(hash);
    }
}
