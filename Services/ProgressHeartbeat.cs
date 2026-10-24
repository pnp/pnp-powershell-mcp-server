using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Diagnostics;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Reports a running command's elapsed time, so the client's idle timer sees it is alive.</summary>
internal sealed class ProgressHeartbeat : IAsyncDisposable
{
    /// <summary>Half the shortest idle timeout clients commonly apply, 60 seconds.</summary>
    internal static TimeSpan Interval =>
        int.TryParse(Environment.GetEnvironmentVariable("PNP_MCP_PROGRESS_INTERVAL_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stop = new();

    private readonly Task _beats;

    internal ProgressHeartbeat(IProgress<ProgressNotificationValue> progress, TimeSpan limit, TimeSpan interval) =>
        _beats = BeatAsync(progress, limit, interval, _stop.Token);

    /// <summary>Null when the client asked for no progress, or the call runs as a task.</summary>
    // A task's tools/call is already answered, and its polling keeps it alive instead.
    internal static ProgressHeartbeat? StartFor(RequestContext<CallToolRequestParams>? context, IProgress<ProgressNotificationValue> progress, TimeSpan limit) =>
        context?.Params?.ProgressToken is null || TaskExecution.RunsAsTask(context)
            ? null
            : new ProgressHeartbeat(progress, limit, Interval);

    private static async Task BeatAsync(IProgress<ProgressNotificationValue> progress, TimeSpan limit, TimeSpan interval, CancellationToken stop)
    {
        var started = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                progress.Report(new ProgressNotificationValue
                {
                    Progress = (float)elapsed.TotalSeconds,
                    Message = $"Still running after {Describe(elapsed)}; the limit is {Describe(limit)}.",
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by DisposeAsync.
        }
        catch (Exception)
        {
            // A beat that cannot be sent must not fail the command it reports on.
        }
    }

    internal static string Describe(TimeSpan span) => span.TotalMinutes < 1
        ? $"{span.Seconds} s"
        : span.Seconds == 0 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalMinutes} min {span.Seconds} s";

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _beats;
        _stop.Dispose();
    }
}
