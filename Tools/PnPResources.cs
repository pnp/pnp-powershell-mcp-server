using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Services;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace PnPPowerShell.MCPServer.Tools;

[McpServerResourceType]
internal sealed class PnPResources
{
    [McpServerResource(
        UriTemplate = "pnp://best-practices",
        Name = "pnp_best_practices",
        Title = "PnP PowerShell best practices",
        MimeType = "text/markdown")]
    [Description("The full guidance document for driving PnP PowerShell through this server: workflow, sessions, authentication, output size, read-only and destructive-command behaviour, and common patterns. Read one section at a time from pnp://best-practices/{section} if the whole document is more than you need.")]
    public static string BestPractices() => PnPPowerShellTools.GetPnpBestPractices();

    [McpServerResource(
        UriTemplate = "pnp://best-practices/{section}",
        Name = "pnp_best_practices_section",
        Title = "PnP PowerShell best practices, one section",
        MimeType = "text/markdown")]
    [Description("One named section of the guidance document, for when the whole thing is more than you need.")]
    public static string BestPracticesSection(
        [Description("Section to return: workflow, docs, sessions, config, readonly, output, destructive, auth, execution, or patterns.")]
        [AllowedValues("workflow", "docs", "sessions", "config", "readonly", "destructive", "auth", "execution", "output", "patterns")]
        string section) => PnPPowerShellTools.GetPnpBestPractices(section);

    [McpServerResource(
        UriTemplate = "pnp://cmdlet/{name}",
        Name = "pnp_cmdlet_docs",
        Title = "PnP PowerShell cmdlet documentation",
        MimeType = "text/plain")]
    [Description("Help text for one PnP PowerShell cmdlet, preceded by its published documentation URL. The name is the full cmdlet name, for example pnp://cmdlet/Get-PnPWeb.")]
    public static Task<string> CmdletDocs(
        PowerShellSessionManager sessions,
        [Description("Full cmdlet name, e.g. Get-PnPWeb.")] string name,
        CancellationToken cancellationToken) =>
        PnPPowerShellTools.GetPnpCommandDocs(sessions, name, cancellationToken);
}
