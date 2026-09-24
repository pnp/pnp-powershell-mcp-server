using ModelContextProtocol;
using System.Collections.Concurrent;

namespace PnPPowerShell.MCPServer.Services;

/// <summary>Owns the named <see cref="PowerShellSession"/> instances the tools execute against.</summary>
// The protocol is stateless but a PnP connection is real state in a real process, so each session is
// addressed by an explicit sessionId handle, which also allows two tenant connections at once.
internal sealed class PowerShellSessionManager : IAsyncDisposable
{
    public const string DefaultSessionId = "default";

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private const int MaxSessions = 10;

    private readonly ConcurrentDictionary<string, PowerShellSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public PowerShellSession Get(string? sessionId)
    {
        EvictIdleSessions();

        // Each session is a pwsh process with PnP loaded, so a caller cannot open them without limit. The
        // default is exempt: docs lookups and diagnosis run there and must not fail on a count of others.
        var id = Normalize(sessionId);
        if (!_sessions.ContainsKey(id) && _sessions.Count >= MaxSessions &&
            !string.Equals(id, DefaultSessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"Too many sessions: {MaxSessions} are open. End one with 'pnp_reset_session', or reuse an existing sessionId.");
        }

        return _sessions.GetOrAdd(id, static key => new PowerShellSession(key));
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

    /// <summary>The session holding a paged result set under this cursor, or null when none does.</summary>
    public PowerShellSession? FindHolder(string? cursor) =>
        string.IsNullOrWhiteSpace(cursor)
            ? null
            : _sessions.Values.FirstOrDefault(s => s.Held?.Cursor == cursor.Trim());

    public IReadOnlyList<(string Id, bool IsAlive, bool IsBusy, DateTimeOffset LastUsedUtc)> Describe()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;
        return [.. _sessions.Values
            .Where(s => s.IsBusy || s.LastUsedUtc >= cutoff)
            .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.Id, s.IsAlive, s.IsBusy, s.LastUsedUtc))];
    }

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
