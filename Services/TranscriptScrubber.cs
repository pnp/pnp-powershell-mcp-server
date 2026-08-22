using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Replaces tenant-identifying and secret material in a transcript before it is written to disk.</summary>
// One instance per recording run. It over-scrubs by choice, and cannot see a display name in free text.
internal sealed partial class TranscriptScrubber
{
    private static readonly string[] TenantNames = ["contoso", "fabrikam", "northwind", "adventureworks"];

    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    private readonly Dictionary<string, string> _tenants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _guids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"-----BEGIN [A-Z ]+-----[\s\S]*?-----END [A-Z ]+-----")]
    private static partial Regex PemBlockRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{16,}")]
    private static partial Regex BearerRegex();

    // PowerShell binds a parameter with either whitespace or a colon, and -ClientSecret:'x' is as valid
    // as -ClientSecret 'x'. Matching only the spaced form let a real secret through untouched.
    [GeneratedRegex(@"(?i)-(ClientSecret|Password|CertificatePassword|CertificateBase64Encoded|AccessToken|SecureString|Token|ApiKey)(\s*:\s*|\s+)('[^']*'|""[^""]*""|\$?[^\s;|,)]+)")]
    private static partial Regex SecretParameterRegex();

    // A profile path carries the operator's account name, and PnP output is full of paths.
    [GeneratedRegex(@"(?i)([A-Za-z]:\\Users\\|/home/|/Users/)([^\\/""'\s,;)]+)")]
    private static partial Regex LocalProfileRegex();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{40}\b")]
    private static partial Regex ThumbprintRegex();

    [GeneratedRegex(@"(?i)\b([a-z0-9][a-z0-9-]{0,62})\.(sharepoint\.com|onmicrosoft\.com|sharepoint\.us|sharepoint\.de|sharepoint\.cn)\b")]
    private static partial Regex TenantHostRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+'-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex IdentityRegex();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b")]
    private static partial Regex GuidRegex();

    /// <summary>Returns <paramref name="text"/> with tenant identity and secrets replaced by stable placeholders.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Secrets first: they can contain anything the later rules would rewrite past recognition.
        // One line, and no quotes: a certificate is usually inside a JSON string, where a real newline
        // would make the fixture unparseable — which is how the replacement is worse than the secret.
        var scrubbed = PemBlockRegex().Replace(text, "-----BEGIN REDACTED----- [redacted-certificate] -----END REDACTED-----");
        scrubbed = JwtRegex().Replace(scrubbed, "[redacted-token]");
        scrubbed = BearerRegex().Replace(scrubbed, "Bearer [redacted-token]");
        scrubbed = SecretParameterRegex().Replace(scrubbed, m => $"-{m.Groups[1].Value} '[redacted-secret]'");
        scrubbed = ThumbprintRegex().Replace(scrubbed, "[redacted-thumbprint]");
        scrubbed = LocalProfileRegex().Replace(scrubbed, m =>
            m.Groups[1].Value + Map(_accounts, m.Groups[2].Value, i => i == 1 ? "localuser" : $"localuser{i}"));

        // Hostnames before identities, so a UPN's domain is already normalised when the UPN is mapped.
        scrubbed = TenantHostRegex().Replace(scrubbed, ReplaceHost);
        scrubbed = IdentityRegex().Replace(scrubbed, m => Map(_identities, m.Value, i => $"user{i}@contoso.onmicrosoft.com"));

        // The all-zeros GUID means "none" rather than an identity, and replacing it destroys that meaning.
        scrubbed = GuidRegex().Replace(scrubbed, m => string.Equals(m.Value, EmptyGuid, StringComparison.Ordinal)
            ? m.Value
            : Map(_guids, m.Value, i => $"00000000-0000-4000-8000-{i:d12}"));

        // Longest first: with "acme" before "acmecorp", the shorter one rewrites the longer one's prefix
        // and the same tenant ends up with two different placeholders.
        foreach (var real in _tenants.Keys.OrderByDescending(k => k.Length).ToList())
        {
            scrubbed = Regex.Replace(scrubbed, Regex.Escape(real), _tenants[real], RegexOptions.IgnoreCase);
        }

        return scrubbed;
    }

    private string ReplaceHost(Match match)
    {
        var label = match.Groups[1].Value;
        var domain = match.Groups[2].Value;

        // "-admin" and "-my" are structural, not identifying, and the connection advice depends on them.
        var suffix = string.Empty;
        foreach (var known in (string[])["-admin", "-my"])
        {
            if (label.EndsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                suffix = known;
                label = label[..^known.Length];
                break;
            }
        }

        // Already a placeholder, or a shared Microsoft host rather than a tenant one.
        if (TenantNames.Contains(label, StringComparer.OrdinalIgnoreCase))
        {
            return match.Value;
        }

        return Map(_tenants, label, i => i <= TenantNames.Length ? TenantNames[i - 1] : $"tenant{i}") + suffix + "." + domain;
    }

    private static string Map(Dictionary<string, string> known, string value, Func<int, string> format)
    {
        if (!known.TryGetValue(value, out var placeholder))
        {
            placeholder = format(known.Count + 1);
            known[value] = placeholder;
        }

        return placeholder;
    }
}
