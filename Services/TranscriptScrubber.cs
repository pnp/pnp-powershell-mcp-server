using System.Text.RegularExpressions;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Removes tenant and secret material before a transcript is written.</summary>
// Over-scrubs by choice; cannot see display names.
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

    // PowerShell also binds -ClientSecret:'x', which a spaced-only match missed.
    [GeneratedRegex(@"(?i)-(ClientSecret|Password|CertificatePassword|CertificateBase64Encoded|AccessToken|SecureString|Token|ApiKey)(\s*:\s*|\s+)('[^']*'|""[^""]*""|\$?[^\s;|,)]+)")]
    private static partial Regex SecretParameterRegex();

    // A whole PowerShell string, escapes included: '' in single quotes, "" or a backtick pair in double quotes.
    // Stopping at an escaped quote would leave the rest of a secret in the clear.
    // Here-strings first: read as an ordinary token, only the opening @' or @" would be taken.
    private const string Literal =
        @"@'[^\S\r\n]*\r?\n[\s\S]*?\r?\n'@|@""[^\S\r\n]*\r?\n[\s\S]*?\r?\n""@|" +
        @"'(?:[^']|'')*'|""(?:[^""`]|`[\s\S]|"""")*""";

    // The audit log's own copy: the transcript pattern above is tied to recorded fixtures.
    // Not inside a name: the -SecureString of ConvertTo-SecureString is not a parameter.
    [GeneratedRegex(@"(?i)(?<![\w-])-(ClientSecret|Password|CertificatePassword|CertificateBase64Encoded|AccessToken|SecureString|Token|ApiKey)(\s*:\s*|\s+)(" + Literal + @"|\$?[^\s;|,)]+)")]
    private static partial Regex AuditSecretParameterRegex();

    // One piece of a ConvertTo-SecureString call: a string, a bracket, or a bare word that is not a parameter or a variable.
    [GeneratedRegex(@"(?<literal>" + Literal + @")|(?<open>\()|(?<close>\))|(?<=\s|\s-\w+:)(?<word>(?![-$@`{\[])[^\s;|()'""]+)")]
    private static partial Regex SecureStringArgumentRegex();

    // The usual way a password is passed: the literal sits inside the call, not after the parameter.
    // Quoted text is taken whole, so a ; or | inside the password does not end the call early.
    [GeneratedRegex(@"(?i)\bConvertTo-SecureString\b(?:" + Literal + @"|`\r?\n|[^;|\r\n'""])*")]
    private static partial Regex SecureStringCallRegex();

    [GeneratedRegex(@"(?i)(" + Literal + @")(\s*\|\s*ConvertTo-SecureString\b)")]
    private static partial Regex PipedSecureStringRegex();

    // A profile path carries the operator's account name, and PnP output is full of paths.
    [GeneratedRegex(@"(?i)([A-Za-z]:\\Users\\|/home/|/Users/)([^\\/""'\s,;)]+)")]
    private static partial Regex LocalProfileRegex();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{40}\b")]
    private static partial Regex ThumbprintRegex();

    // app_displayname, the one display name recorded on purpose. Matched as a JSON string, escapes included.
    [GeneratedRegex(@"""app""\s*:\s*""(?:[^""\\]|\\.)*""")]
    private static partial Regex AppDisplayNameRegex();

    [GeneratedRegex(@"(?i)\b([a-z0-9][a-z0-9-]{0,62})\.(sharepoint\.com|onmicrosoft\.com|sharepoint\.us|sharepoint\.de|sharepoint\.cn)\b")]
    private static partial Regex TenantHostRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+'-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex IdentityRegex();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b")]
    private static partial Regex GuidRegex();

    /// <summary>Removes secrets only, leaving tenant names and ids readable.</summary>
    public static string Secrets(string text)
    {
        var scrubbed = PemBlockRegex().Replace(text, "[redacted-certificate]");
        scrubbed = JwtRegex().Replace(scrubbed, "[redacted-token]");
        scrubbed = BearerRegex().Replace(scrubbed, "Bearer [redacted-token]");
        scrubbed = SecureStringCallRegex().Replace(scrubbed, m => RedactSecureStringCall(m.Value));
        scrubbed = PipedSecureStringRegex().Replace(scrubbed, m => "'[redacted-secret]'" + m.Groups[2].Value);

        return AuditSecretParameterRegex().Replace(scrubbed, m => $"-{m.Groups[1].Value} '[redacted-secret]'");
    }

    /// <summary>Redacts every string and bare word the call is given, in any order: the password need not be quoted or first.</summary>
    // Stops at the bracket that closes the call, so the rest of the statement is left readable.
    private static string RedactSecureStringCall(string call)
    {
        var depth = 0;
        var closed = false;

        return SecureStringArgumentRegex().Replace(call, m =>
        {
            if (closed)
            {
                return m.Value;
            }

            if (m.Groups["open"].Success)
            {
                depth++;
                return m.Value;
            }

            if (m.Groups["close"].Success)
            {
                closed = --depth < 0;
                return m.Value;
            }

            return "'[redacted-secret]'";
        });
    }

    /// <summary>Returns <paramref name="text"/> with tenant identity and secrets replaced by stable placeholders.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Secrets first; one line, since a newline breaks the JSON around them.
        var scrubbed = PemBlockRegex().Replace(text, "-----BEGIN REDACTED----- [redacted-certificate] -----END REDACTED-----");
        scrubbed = JwtRegex().Replace(scrubbed, "[redacted-token]");
        scrubbed = BearerRegex().Replace(scrubbed, "Bearer [redacted-token]");
        scrubbed = SecretParameterRegex().Replace(scrubbed, m => $"-{m.Groups[1].Value} '[redacted-secret]'");
        scrubbed = ThumbprintRegex().Replace(scrubbed, "[redacted-thumbprint]");
        scrubbed = AppDisplayNameRegex().Replace(scrubbed, @"""app"":""[redacted-app-name]""");
        scrubbed = LocalProfileRegex().Replace(scrubbed, m =>
            m.Groups[1].Value + Map(_accounts, m.Groups[2].Value, i => i == 1 ? "localuser" : $"localuser{i}"));

        // Hostnames before identities, so a UPN's domain is already normalised when the UPN is mapped.
        scrubbed = TenantHostRegex().Replace(scrubbed, ReplaceHost);
        scrubbed = IdentityRegex().Replace(scrubbed, m => Map(_identities, m.Value, i => $"user{i}@contoso.onmicrosoft.com"));

        // The all-zeros GUID means "none" rather than an identity, and replacing it destroys that meaning.
        scrubbed = GuidRegex().Replace(scrubbed, m => string.Equals(m.Value, EmptyGuid, StringComparison.Ordinal)
            ? m.Value
            : Map(_guids, m.Value, i => $"00000000-0000-4000-8000-{i:d12}"));

        // Longest first, or "acme" rewrites the prefix of "acmecorp".
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
