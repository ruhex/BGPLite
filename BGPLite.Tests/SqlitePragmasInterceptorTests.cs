using BGPLite.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Tests;

/// <summary>
/// Verifies the connection-level SQLite PRAGMAs applied by <see cref="SqlitePragmasInterceptor"/>
/// (#95): WAL mode, a 5s busy_timeout, and synchronous=NORMAL. Uses a temp file because WAL does
/// not apply to an in-memory database.
/// </summary>
public class SqlitePragmasInterceptorTests
{
    [Fact]
    public async Task Connection_Open_Applies_Wal_BusyTimeout_Synchronous()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bgplite-pragma-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<BgpDbContext>()
                .UseSqlite($"Data Source={file}")
                .AddInterceptors(new SqlitePragmasInterceptor())
                .Options;

            await using var db = new BgpDbContext(options);
            await db.Database.OpenConnectionAsync(); // triggers ConnectionOpenedAsync → PRAGMAs

            var conn = (SqliteConnection)db.Database.GetDbConnection();
            Assert.Equal("wal", Scalar(conn, "PRAGMA journal_mode;"));
            Assert.Equal("5000", Scalar(conn, "PRAGMA busy_timeout;"));
            Assert.Equal("1", Scalar(conn, "PRAGMA synchronous;")); // NORMAL = 1
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + "-wal");
            File.Delete(file + "-shm");
        }
    }

    private static string Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }
}
