using ModelContextProtocol.Protocol;
using System.Reflection;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>What the server calls itself in serverInfo: the SDK's default name and version, plus what a client can show.</summary>
internal static class ServerIdentity
{
    internal const string Title = "PnP PowerShell";

    internal const string Website = "https://github.com/pnp/pnp-powershell-mcp-server";

    // Parker, from pnp/media. A URL, not a data URI: the image is 54 KB and this is sent on every connect.
    internal const string IconUrl = "https://raw.githubusercontent.com/pnp/media/master/parker/pnp/300w/parker.png";

    internal static Implementation Create()
    {
        var assembly = typeof(ServerIdentity).Assembly;

        // The package version, as pnp_ping reports it, without the commit: "0.1.7-beta", not "0.1.7.0".
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

        return new Implementation
        {
            Name = assembly.GetName().Name ?? "PnPPowerShell.MCPServer",
            Version = string.IsNullOrWhiteSpace(version) ? assembly.GetName().Version?.ToString() ?? "1.0.0" : version,
            Title = Title,
            Description = "Runs PnP PowerShell against Microsoft 365 and helps author PnP PowerShell scripts.",
            WebsiteUrl = Website,
            Icons = [new Icon { Source = IconUrl, MimeType = "image/png", Sizes = ["300x392"] }],
        };
    }
}
