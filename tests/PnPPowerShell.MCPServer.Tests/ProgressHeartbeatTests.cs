using ModelContextProtocol;
using PnPPowerShell.MCPServer.Services;
using System.Collections.Concurrent;

namespace PnPPowerShell.MCPServer.Tests;

public class ProgressHeartbeatTests
{
    private sealed class Recorder : IProgress<ProgressNotificationValue>
    {
        public ConcurrentQueue<ProgressNotificationValue> Reports { get; } = new();

        public void Report(ProgressNotificationValue value) => Reports.Enqueue(value);
    }

    [Fact]
    public async Task It_reports_rising_elapsed_time_until_disposed()
    {
        var recorder = new Recorder();

        await using (new ProgressHeartbeat(recorder, TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(50)))
        {
            await Task.Delay(500);
        }

        var reports = recorder.Reports.ToArray();
        Assert.True(reports.Length >= 2, $"Expected at least two beats, got {reports.Length}.");
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.Progress > p.First.Progress), "Progress must rise with every notification.");
        Assert.All(reports, r => Assert.Contains("the limit is 10 min", r.Message, StringComparison.Ordinal));
        Assert.All(reports, r => Assert.Null(r.Total));

        await Task.Delay(200);
        Assert.Equal(reports.Length, recorder.Reports.Count);
    }

    private sealed class Broken : IProgress<ProgressNotificationValue>
    {
        public int Attempts;

        public void Report(ProgressNotificationValue value)
        {
            Attempts++;
            throw new ObjectDisposedException("transport");
        }
    }

    [Fact]
    public async Task A_beat_that_cannot_be_sent_stops_the_heartbeat_without_failing()
    {
        var broken = new Broken();

        var heartbeat = new ProgressHeartbeat(broken, TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(20));
        await Task.Delay(300);
        await heartbeat.DisposeAsync();

        Assert.Equal(1, broken.Attempts);
    }

    [Fact]
    public void Without_a_request_there_is_no_heartbeat()
    {
        Assert.Null(ProgressHeartbeat.StartFor(null, new Recorder(), TimeSpan.FromMinutes(10)));
    }

    [Theory]
    [InlineData(5, "5 s")]
    [InlineData(60, "1 min")]
    [InlineData(95, "1 min 35 s")]
    [InlineData(3600, "60 min")]
    public void Durations_read_naturally(int seconds, string expected)
    {
        Assert.Equal(expected, ProgressHeartbeat.Describe(TimeSpan.FromSeconds(seconds)));
    }
}
