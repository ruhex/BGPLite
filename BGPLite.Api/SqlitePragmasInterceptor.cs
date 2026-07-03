using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BGPLite.Api;

/// <summary>
/// Applies connection-level SQLite PRAGMAs the moment EF Core opens a connection, so every
/// <see cref="BgpDbContext"/> (<c>IDbContextFactory</c> creates one per PeerStore operation) gets
/// them. Resilience + concurrency for the peer store (#95):
/// <list type="bullet">
/// <item><c>journal_mode=WAL</c> — writers no longer block readers (and vice-versa); massively cuts
/// writer contention. Persists in the DB file, so re-applying is an idempotent no-op.</item>
/// <item><c>synchronous=NORMAL</c> — the standard WAL companion: keeps durability for committed WAL
/// frames while cutting fsync cost vs FULL. Peer state is reconstructable from BGP sessions, so
/// NORMAL is the right tradeoff.</item>
/// <item><c>busy_timeout=5000</c> — SQLite itself waits and retries the lock for up to 5s before
/// returning <c>SQLITE_BUSY</c>. This is the engine-level retry #95 asks for; a separate app-level
/// Polly loop would only double-retry the rare &gt;5s contention.</item>
/// </list>
/// <c>synchronous</c>/<c>busy_timeout</c> are per-connection, so they are re-applied on every open.
/// </summary>
public sealed class SqlitePragmasInterceptor : DbConnectionInterceptor
{
    private const int BusyTimeoutMs = 5000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplyPragmas(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await ApplyPragmasAsync(connection, cancellationToken);

    private static void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;
        // Microsoft.Data.Sqlite runs only the first statement of a multi-statement command, so issue
        // each PRAGMA separately.
        Execute(sqlite, $"PRAGMA journal_mode=WAL;");
        Execute(sqlite, "PRAGMA synchronous=NORMAL;");
        Execute(sqlite, $"PRAGMA busy_timeout={BusyTimeoutMs};");
    }

    private static async Task ApplyPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not SqliteConnection sqlite) return;
        await ExecuteAsync(sqlite, $"PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(sqlite, "PRAGMA synchronous=NORMAL;", cancellationToken);
        await ExecuteAsync(sqlite, $"PRAGMA busy_timeout={BusyTimeoutMs};", cancellationToken);
    }

    private static void Execute(SqliteConnection sqlite, string pragma)
    {
        using var cmd = sqlite.CreateCommand();
        cmd.CommandText = pragma;
        cmd.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(SqliteConnection sqlite, string pragma, CancellationToken cancellationToken)
    {
        using var cmd = sqlite.CreateCommand();
        cmd.CommandText = pragma;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
