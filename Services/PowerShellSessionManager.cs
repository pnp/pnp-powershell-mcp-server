using System.Collections.Concurrent;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Owns the named <see cref="PowerShellSession"/> instances the tools execute against.</summary>
// The protocol is stateless but a PnP connection is real state in a real process, so each session is
// addressed by an explicit sessionId handle, which also allows two tenant connections at once.
internal sealed class PowerShellSessionManager : IAsyncDisposable
{
    public const string DefaultSessionId = "default";

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, PowerShellSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public PowerShellSession Get(string? sessionId)
    {
        EvictIdleSessions();
        return _sessions.GetOrAdd(Normalize(sessionId), static id => new PowerShellSession(id));
    }

    public async Task<bool> ResetAsync(string? sessionId)
    {
        if (!_sessions.TryGetValue(Normalize(sessionId), out var session))
        {
            return false;
        }

        await session.ResetAsync();
        return true;
    }

    public IReadOnlyList<(string Id, bool IsAlive, DateTimeOffset LastUsedUtc)> Describe() =>
        [.. _sessions.Values
            .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.Id, s.IsAlive, s.LastUsedUtc))];

    private static string Normalize(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? DefaultSessionId : sessionId.Trim();

    /// <summary>Ends unused sessions so an abandoned connection does not outlive its usefulness.</summary>
    private void EvictIdleSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;

        foreach (var session in _sessions.Values)
        {
            // A command that runs longer than the idle window is still working, not abandoned:
            // LastUsedUtc only advances when it finishes, so busy sessions must be skipped explicitly.
            if (session.IsBusy || session.LastUsedUtc >= cutoff || !_sessions.TryRemove(session.Id, out var removed))
            {
                continue;
            }

            _ = removed.DisposeAsync().AsTask();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
    }
}
