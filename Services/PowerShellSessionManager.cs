using System.Collections.Concurrent;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>
/// Owns the named <see cref="PowerShellSession"/> instances the tools execute against.
/// </summary>
/// <remarks>
/// The MCP protocol is stateless, but the application it fronts is not: a PnP connection is real
/// state living in a real process. Rather than hiding that behind an implicit session, each session
/// is addressed by an explicit <c>sessionId</c> handle that the caller passes back on later calls —
/// which is also what makes it possible to hold connections to two tenants at once.
/// </remarks>
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

    /// <summary>
    /// Ends sessions that have gone unused, so an abandoned connection does not keep a pwsh process
    /// (and its tenant connection) alive for the lifetime of the server.
    /// </summary>
    private void EvictIdleSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;

        foreach (var session in _sessions.Values)
        {
            if (session.LastUsedUtc >= cutoff || !_sessions.TryRemove(session.Id, out var removed))
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
