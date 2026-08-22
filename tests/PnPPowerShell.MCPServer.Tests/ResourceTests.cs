using ModelContextProtocol.Server;
using PnPPowerShell.MCPServer.Tools;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace PnPPowerShell.MCPServer.Tests;

public class ResourceTests
{
    [Fact]
    public void The_advertised_uri_space_is_what_clients_are_told_to_browse()
    {
        var templates = typeof(PnPResources)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerResourceAttribute>()?.UriTemplate)
            .Where(t => t is not null);

        Assert.Equal(
            ["pnp://best-practices", "pnp://best-practices/{section}", "pnp://cmdlet/{name}"],
            templates.Order());
    }

    [Fact]
    public void The_section_resource_offers_exactly_the_sections_that_exist()
    {
        var allowed = typeof(PnPResources)
            .GetMethod(nameof(PnPResources.BestPracticesSection))!
            .GetParameters()
            .Single(p => p.Name == "section")
            .GetCustomAttribute<AllowedValuesAttribute>()!
            .Values
            .Select(v => (string)v!);

        Assert.Equal(PnPPowerShellTools.BestPracticeSections.Keys.Order(), allowed.Order());
    }
}
