using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    public void Tools_are_listed_in_the_declared_order()
    {
        _client.Initialize();

        var names = _client.Request("tools/list").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString());

        Assert.Equal(ToolOrder.Names, names);
    }

    /// <summary>VS Code and Codex give the model only structured content, so it must carry the text too.</summary>
    [Theory]
    [InlineData("pnp_search_commands", """{"query":"create a site"}""")]
    [InlineData("pnp_search_commands", """{"query":"zzqx wqzz"}""")]
    [InlineData("pnp_search_script_samples", """{"query":"document set","limit":3}""")]
    [InlineData("pnp_list_sessions", "{}")]
    public void Structured_content_carries_the_text_it_accompanies(string tool, string arguments)
    {
        _client.Initialize();

        var call = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(arguments) };
        var result = _client.Request("tools/call", call.ToJsonString());
        var text = string.Concat(result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));

        Assert.False(string.IsNullOrEmpty(text));
        Assert.Equal(text, result.GetProperty("structuredContent").GetProperty("text").GetString());
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

    /// <summary>Before 2026-07-28 the prompt is a server-to-client elicitation/create request, as VS Code receives it.</summary>
    [RequiresPnPTheory]
    [InlineData("2025-06-18", false)]
    [InlineData("2025-11-25", true)]
    public void A_destructive_command_on_a_legacy_connection_asks_through_elicitation_create(string protocolVersion, bool confirm)
    {
        using var client = new McpStdioClient();
        var asked = new List<string>();
        client.OnServerRequest = (method, _) =>
        {
            asked.Add(method);
            return Answer(confirm);
        };
        client.Initialize(protocolVersion, capabilities: """{"elicitation":{}}""");

        var outcome = client.CallTool("pnp_run_command", $$"""{"command":"{{UnknownListRemoval()}}"}""");

        Assert.Equal(["elicitation/create"], asked);
        if (confirm)
        {
            Assert.DoesNotContain("Cancelled", outcome, StringComparison.Ordinal);
            Assert.Contains("Connect-PnPOnline", outcome, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("was not explicitly confirmed, so nothing was run", outcome, StringComparison.Ordinal);
        }
    }

    /// <summary>On 2026-07-28 without tasks, the prompt returns as input_required and the answer as a retry.</summary>
    [RequiresPnPFact]
    public void A_destructive_command_outside_a_task_is_answered_by_a_retry()
    {
        using var client = new McpStdioClient();
        client.Discover();

        var call = new JsonObject { ["name"] = "pnp_run_command", ["arguments"] = new JsonObject { ["command"] = UnknownListRemoval() } };
        var asked = client.Request("tools/call", McpStdioClient.WithMeta(call, tasks: false));

        Assert.Equal("input_required", asked.GetProperty("resultType").GetString());

        call["inputResponses"] = new JsonObject { ["confirmDestructive"] = Answer(confirm: false) };
        call["requestState"] = asked.GetProperty("requestState").GetString();

        Assert.Contains("was not explicitly confirmed, so nothing was run", ResultText(client.Request("tools/call", McpStdioClient.WithMeta(call, tasks: false))), StringComparison.Ordinal);
    }

    /// <summary>As Claude Code meets it: the call becomes a task that asks through tasks/get and tasks/update.</summary>
    [RequiresPnPFact]
    public void A_destructive_command_run_as_a_task_is_not_run_when_the_box_is_unticked()
    {
        Assert.Contains("was not explicitly confirmed, so nothing was run", RunDestructiveTask(confirm: false), StringComparison.Ordinal);
    }

    [RequiresPnPFact]
    public void A_destructive_task_is_refused_when_the_client_offers_only_url_elicitation()
    {
        using var client = new McpStdioClient();
        client.Discover();

        var call = new JsonObject { ["name"] = "pnp_run_command", ["arguments"] = new JsonObject { ["command"] = UnknownListRemoval() } };
        var created = client.Request("tools/call", McpStdioClient.WithMeta(call, tasks: true, elicitation: "url"));

        var outcome = ResultText(PollTask(client, created.GetProperty("taskId").GetString()!, "completed").GetProperty("result"));

        Assert.Contains("Blocked", outcome, StringComparison.Ordinal);
    }

    [RequiresPnPFact]
    public void A_destructive_command_run_as_a_task_runs_once_confirmed()
    {
        var outcome = RunDestructiveTask(confirm: true);

        // Nothing is connected, so PnP itself refuses the command; reaching PnP proves the gate let it through.
        Assert.DoesNotContain("Cancelled", outcome, StringComparison.Ordinal);
        Assert.Contains("Connect-PnPOnline", outcome, StringComparison.Ordinal);
    }

    /// <summary>A long call reports progress, which resets idle timers such as Claude Code's.</summary>
    [RequiresPnPFact]
    public void A_long_command_reports_progress_against_the_callers_token()
    {
        using var client = new McpStdioClient(("PNP_MCP_PROGRESS_INTERVAL_SECONDS", "1"));
        client.Discover();

        client.Request("tools/call", McpStdioClient.WithMeta(SlowCommand(), tasks: false, progressToken: "heartbeat"));

        var beats = client.Notifications
            .Where(n => n.GetProperty("method").GetString() == "notifications/progress")
            .Select(n => n.GetProperty("params"))
            .ToList();

        Assert.True(beats.Count >= 2, $"Expected at least two progress notifications, got {beats.Count}.");
        Assert.All(beats, b => Assert.Equal("heartbeat", b.GetProperty("progressToken").GetString()));
        Assert.All(beats, b => Assert.StartsWith("Still running after", b.GetProperty("message").GetString(), StringComparison.Ordinal));
    }

    [RequiresPnPFact]
    public void A_task_sends_no_progress_after_its_call_was_answered()
    {
        using var client = new McpStdioClient(("PNP_MCP_PROGRESS_INTERVAL_SECONDS", "1"));
        client.Discover();

        var created = client.Request("tools/call", McpStdioClient.WithMeta(SlowCommand(), tasks: true, progressToken: "heartbeat"));
        PollTask(client, created.GetProperty("taskId").GetString()!, "completed");

        Assert.DoesNotContain(client.Notifications, n => n.GetProperty("method").GetString() == "notifications/progress");
    }

    // Read-only and harmless: nothing is connected, so Get-PnPConnection only reports that.
    private static JsonObject SlowCommand() =>
        new() { ["name"] = "pnp_run_command", ["arguments"] = new JsonObject { ["command"] = "Start-Sleep -Seconds 4; Get-PnPConnection" } };

    // Names a list that cannot exist, so even a connected session would have nothing to remove.
    private static string UnknownListRemoval() => $"Remove-PnPList -Identity 'pnp-mcp-test-{Guid.NewGuid():N}' -Force";

    private static JsonObject Answer(bool confirm) =>
        new() { ["action"] = "accept", ["content"] = new JsonObject { ["confirm"] = confirm } };

    private static string ResultText(JsonElement result) =>
        string.Concat(result.GetProperty("content").EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "text")
            .Select(c => c.GetProperty("text").GetString()));

    private static string RunDestructiveTask(bool confirm)
    {
        using var client = new McpStdioClient();
        client.Discover();

        var command = UnknownListRemoval();
        var call = new JsonObject { ["name"] = "pnp_run_command", ["arguments"] = new JsonObject { ["command"] = command } };
        var created = client.Request("tools/call", McpStdioClient.WithMeta(call, tasks: true));

        Assert.Equal("task", created.GetProperty("resultType").GetString());
        Assert.Equal((long)TaskExecution.TimeToLive.TotalMilliseconds, created.GetProperty("ttlMs").GetInt64());
        var taskId = created.GetProperty("taskId").GetString()!;

        var asked = PollTask(client, taskId, "input_required");
        var prompt = Assert.Single(asked.GetProperty("inputRequests").EnumerateObject());

        Assert.Equal("elicitation/create", prompt.Value.GetProperty("method").GetString());
        Assert.Contains(command, prompt.Value.GetProperty("params").GetProperty("message").GetString(), StringComparison.Ordinal);

        var update = new JsonObject { ["taskId"] = taskId, ["inputResponses"] = new JsonObject { [prompt.Name] = Answer(confirm) } };
        client.Request("tasks/update", McpStdioClient.WithMeta(update, tasks: true));

        return ResultText(PollTask(client, taskId, "completed").GetProperty("result"));
    }

    /// <summary>Polls tasks/get until the task reaches <paramref name="status"/>, failing on any other end state.</summary>
    private static JsonElement PollTask(McpStdioClient client, string taskId, string status)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            var task = client.Request("tasks/get", McpStdioClient.WithMeta(new JsonObject { ["taskId"] = taskId }, tasks: true));
            var current = task.GetProperty("status").GetString();

            if (current == status)
            {
                return task;
            }

            Assert.True(current is "working" or "input_required", $"Task ended '{current}' while waiting for '{status}': {task}");
            Thread.Sleep(250);
        }

        throw new TimeoutException($"Task {taskId} did not reach '{status}' within 60 seconds.");
    }
}

/// <summary>Minimal MCP client: newline-delimited JSON-RPC over stdio.</summary>
// Hand-rolled, so the wire format is tested rather than the SDK.
internal sealed class McpStdioClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly Process _server;
    private readonly List<JsonElement> _notifications = [];
    private int _id;

    /// <summary>Every notification the server sent while a request was awaited.</summary>
    public IReadOnlyList<JsonElement> Notifications => _notifications;

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

    /// <summary>Answers a request the server sends, such as elicitation/create on a legacy connection.</summary>
    public Func<string, JsonElement, JsonNode>? OnServerRequest { get; set; }

    public JsonElement Initialize(string protocolVersion = "2025-06-18", string capabilities = "{}")
    {
        var result = Request("initialize",
            $$$"""{"protocolVersion":"{{{protocolVersion}}}","capabilities":{{{capabilities}}},"clientInfo":{"name":"stdio-protocol-tests","version":"1.0"}}""");

        Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        return result;
    }

    /// <summary>The 2026-07-28 opening: no initialize, and capabilities travel on every request instead.</summary>
    public JsonElement Discover() => Request("server/discover", WithMeta(new JsonObject(), tasks: false));

    /// <summary>Adds the per-request _meta a 2026-07-28 client sends; with tasks, shaped as Claude Code's.</summary>
    public static string WithMeta(JsonObject parameters, bool tasks, string elicitation = "form", string? progressToken = null)
    {
        var capabilities = new JsonObject { ["elicitation"] = new JsonObject { [elicitation] = new JsonObject() } };
        if (tasks)
        {
            capabilities["extensions"] = new JsonObject { ["io.modelcontextprotocol/tasks"] = new JsonObject() };
        }

        var copy = parameters.DeepClone().AsObject();
        copy["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "stdio-protocol-tests", ["version"] = "1.0" },
            ["io.modelcontextprotocol/clientCapabilities"] = capabilities,
        };

        if (progressToken is not null)
        {
            copy["_meta"]!["progressToken"] = progressToken;
        }

        return copy.ToJsonString();
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

            if (!message.TryGetProperty("id", out var responseId))
            {
                _notifications.Add(message.Clone());
                continue;
            }

            if (message.TryGetProperty("method", out var serverMethod))
            {
                var answer = OnServerRequest?.Invoke(serverMethod.GetString()!, message.GetProperty("params"))
                    ?? throw new InvalidOperationException($"The server sent an unexpected '{serverMethod}' request.");

                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(responseId.GetRawText()), ["result"] = answer }.ToJsonString());
                continue;
            }

            // Skip any response that is not the one we asked for.
            if (responseId.GetInt32() != id)
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
