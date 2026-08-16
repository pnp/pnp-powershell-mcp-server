using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Tasks;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;

var builder = Host.CreateApplicationBuilder(args);

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Holds the long-lived pwsh sessions the tools execute against, so a PnP connection survives
// across tool calls. Singleton: the sessions are the server's state, not a per-request concern.
builder.Services.AddSingleton<PowerShellSessionManager>();

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTasks(
        new InMemoryMcpTaskStore(),
        options =>
        {
            // Long-running tenant operations are the reason Tasks is here: a client that supports the
            // extension can start the call and poll, instead of holding a request open. Metadata
            // lookups are quick and stay synchronous so short calls do not pay for a task round-trip.
            options.ExecutionModeSelector = request => request.Params?.Name switch
            {
                "pnp_run_command" => McpTaskExecutionMode.Optional,
                _ => McpTaskExecutionMode.Synchronous,
            };
        })
    .WithTools<PnPPowerShellTools>()
    .WithTools<ScriptSampleTools>();

await builder.Build().RunAsync();
