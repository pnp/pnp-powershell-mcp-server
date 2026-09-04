using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Drives the built server as a real process over JSON-RPC, the way a client does.</summary>
public sealed class StdioProtocolTests : IDisposable
{
    private readonly McpStdioClient _client = new();

    public void Dispose() => _client.Dispose();

    [Fact]
    public void The_server_starts_and_negotiates()
    {
        var result = _client.Initialize();

        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("protocolVersion").GetString()));
        Assert.Equal("PnPPowerShell.MCPServer", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _), "The server advertises no tools capability.");
    }

    [Fact]
    public void The_server_publishes_instructions_naming_both_non_assumptions()
    {
        var instructions = _client.Initialize().GetProperty("instructions").GetString();

        Assert.NotNull(instructions);
        Assert.Contains("never assume an environment variable, an app registration or a persisted login exists", instructions, StringComparison.Ordinal);
        Assert.Contains("before handing out either", instructions, StringComparison.Ordinal);
        Assert.Contains("say what the default grant is", instructions, StringComparison.Ordinal);
        Assert.Contains("hand the commands to the user instead of running them", instructions, StringComparison.Ordinal);
        Assert.InRange(instructions.Length, 1, 1500);
    }

    [Fact]
    public void Every_tool_is_published_with_a_description_and_annotations()
    {
        _client.Initialize();

        var tools = _client.Request("tools/list").GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Equal(ToolCatalog.All.Select(t => t.ProtocolTool.Name).Order(), names.Order());

        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();

            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()), $"{name} has no description.");
            Assert.True(tool.TryGetProperty("inputSchema", out _), $"{name} has no inputSchema.");
            Assert.True(tool.TryGetProperty("annotations", out var annotations), $"{name} has no annotations.");
            Assert.True(annotations.TryGetProperty("readOnlyHint", out _), $"{name} does not state readOnlyHint over the wire.");
        }
    }

    [Fact]
    public void The_guidance_resources_are_published()
    {
        _client.Initialize();

        var templates = _client.Request("resources/templates/list").GetProperty("resourceTemplates")
            .EnumerateArray().Select(r => r.GetProperty("uriTemplate").GetString()).ToList();

        Assert.Contains("pnp://best-practices/{section}", templates);
        Assert.Contains("pnp://cmdlet/{name}", templates);
    }

    [Theory]
    [InlineData("pnp_get_best_practices", """{"section":"workflow"}""", "Recommended Workflow")]
    [InlineData("pnp_search_script_samples", """{"query":"document set","limit":3}""", "**Name**:")]
    [InlineData("pnp_get_result_page", """{"cursor":"nope"}""", "No held result set")]
    public void A_tool_call_returns_the_expected_content(string tool, string arguments, string expected)
    {
        _client.Initialize();

        Assert.Contains(expected, _client.CallTool(tool, arguments), StringComparison.Ordinal);
    }

    /// <summary>The full command path — analysis, policy, execution — over the wire, answered from fixtures.</summary>
    [PlaybackFact]
    public void A_command_runs_end_to_end_over_the_wire()
    {
        using var client = new McpStdioClient(("PNP_MCP_REPLAY_DIR", Path.Combine(AppContext.BaseDirectory, "fixtures")));
        client.Initialize();

        Assert.Contains("Url", client.CallTool("pnp_run_command", """{"command":"Get-PnPWeb | Select-Object Title, Url"}"""), StringComparison.Ordinal);
    }

    /// <summary>The confirmation gate, over the wire, against a client that cannot be prompted.</summary>
    // Analysis must run; nothing is executed.
    [RequiresPnPFact]
    public void A_destructive_command_is_refused_when_the_client_cannot_be_prompted()
    {
        using var client = new McpStdioClient();
        client.Initialize();

        var result = client.CallTool("pnp_run_command", """{"command":"Remove-PnPTenantSite -Url 'https://contoso.sharepoint.com/sites/x'"}""");

        Assert.Contains("Blocked", result, StringComparison.Ordinal);
        Assert.Contains("Remove-PnPTenantSite", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Command completed", result, StringComparison.Ordinal);
    }
}

/// <summary>Minimal MCP client: newline-delimited JSON-RPC over stdio.</summary>
// Hand-rolled, so the wire format is tested rather than the SDK.
internal sealed class McpStdioClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly Process _server;
    private int _id;

    public McpStdioClient(params (string Name, string Value)[] environment)
    {
        var executable = FindServerExecutable();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        _server = Process.Start(startInfo) ?? throw new InvalidOperationException("The server process did not start.");

        // Drained so a full stderr pipe cannot deadlock the child.
        _ = Task.Run(() => _server.StandardError.ReadToEnd());
    }

    /// <summary>The server's own build output, where its self-contained runtime lives.</summary>
    // The copy beside the tests will not start.
    private static string FindServerExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "PnPPowerShell.MCPServer.exe" : "PnPPowerShell.MCPServer";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PnPPowerShell.MCPServer.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root from the test output directory.");

        var candidates = Directory
            .EnumerateFiles(Path.Combine(directory!.FullName, "bin"), name, SearchOption.AllDirectories)
            .Where(p => File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "PnPPowerShell.MCPServer.runtimeconfig.json")))
            .ToList();

        Assert.True(candidates.Count > 0, $"No built server found under {directory.FullName}/bin. Run 'dotnet build' first.");

        // Match configuration and RID, or this drives a stale or foreign binary.
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var matched = candidates
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Contains(RuntimeInformation.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        Assert.True(
            matched.Count > 0,
            $"No {configuration} server built for {RuntimeInformation.RuntimeIdentifier}. Found only:\n  {string.Join("\n  ", candidates)}");

        return matched[0];
    }

    public JsonElement Initialize()
    {
        var result = Request("initialize",
            """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdio-protocol-tests","version":"1.0"}}""");

        Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        return result;
    }

    public string CallTool(string name, string arguments)
    {
        var result = Request("tools/call", $$"""{"name":"{{name}}","arguments":{{arguments}}}""");

        return string.Concat(result.GetProperty("content").EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "text")
            .Select(c => c.GetProperty("text").GetString()));
    }

    public JsonElement Request(string method, string? parameters = null)
    {
        var id = ++_id;
        Send(parameters is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");

        var deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline)
        {
            var line = ReadLine(deadline - DateTime.UtcNow)
                ?? throw new InvalidOperationException($"The server closed stdout while waiting for '{method}'.");

            if (line.Length == 0)
            {
                continue;
            }

            var message = JsonDocument.Parse(line).RootElement;

            // Skip notifications and any response that is not the one we asked for.
            if (!message.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
            {
                continue;
            }

            if (message.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"'{method}' failed: {error}");
            }

            return message.GetProperty("result").Clone();
        }

        throw new TimeoutException($"No response to '{method}' within {Timeout.TotalSeconds:0} seconds.");
    }

    private void Send(string message)
    {
        _server.StandardInput.WriteLine(message);
        _server.StandardInput.Flush();
    }

    private string? ReadLine(TimeSpan within)
    {
        var read = _server.StandardOutput.ReadLineAsync();

        return read.Wait(within) ? read.Result : throw new TimeoutException("The server sent nothing in time.");
    }

    public void Dispose()
    {
        try
        {
            if (!_server.HasExited)
            {
                _server.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _server.Dispose();
    }
}
