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

    private readonly Lock _admission = new();

    // Named sessions removed but whose process may still be alive.
    private int _retiring;

    private readonly ConcurrentDictionary<string, PowerShellSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public PowerShellSession Get(string? sessionId)
    {
        EvictIdleSessions();

        var id = Normalize(sessionId);
        if (_sessions.TryGetValue(id, out var existing))
        {
            return existing;
        }

        // Each session is a pwsh process with PnP loaded, so named ones are capped, and admitted under a lock
        // so concurrent calls cannot overshoot. The default neither counts nor is refused: docs and diagnosis run there.
        lock (_admission)
        {
            if (_sessions.TryGetValue(id, out existing))
            {
                return existing;
            }

            var isDefault = IsDefault(id);
            var named = _sessions.Count - (_sessions.ContainsKey(DefaultSessionId) ? 1 : 0) + Volatile.Read(ref _retiring);
            if (!isDefault && named >= MaxSessions)
            {
                throw new McpException(
                    $"Too many sessions: {MaxSessions} named sessions are open or still ending. End one with 'pnp_reset_session', or reuse an existing sessionId.");
            }

            var created = new PowerShellSession(id);
            _sessions[id] = created;
            return created;
        }
    }

    public async Task<bool> ResetAsync(string? sessionId)
    {
        // Removed, not just terminated, so a reset frees its slot under the cap once the process is gone.
        if (Retire(Normalize(sessionId)) is not { } retiring)
        {
            return false;
        }

        await retiring;
        return true;
    }

    /// <summary>Removes a session, counting it against the cap until its process is gone; null when there was none.</summary>
    // Under the admission lock, so no replacement is admitted between the removal and the count. Given an
    // expected instance, only that one is removed, so eviction cannot retire a replacement created since.
    private Task? Retire(string id, PowerShellSession? expected = null)
    {
        var session = expected;
        var counted = !IsDefault(id);

        lock (_admission)
        {
            var removed = session is null
                ? _sessions.TryRemove(id, out session)
                : _sessions.TryRemove(KeyValuePair.Create(id, session));

            if (!removed || session is null)
            {
                return null;
            }

            if (counted)
            {
                Interlocked.Increment(ref _retiring);
            }
        }

        return Finish();

        async Task Finish()
        {
            try
            {
                await session.RetireAsync();
            }
            finally
            {
                if (counted)
                {
                    Interlocked.Decrement(ref _retiring);
                }
            }
        }
    }

    private static bool IsDefault(string id) => string.Equals(id, DefaultSessionId, StringComparison.OrdinalIgnoreCase);

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
            if (session.IsBusy || session.LastUsedUtc >= cutoff)
            {
                continue;
            }

            _ = Retire(session.Id, session);
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
