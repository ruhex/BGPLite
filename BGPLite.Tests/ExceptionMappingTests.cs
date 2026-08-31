using Microsoft.Data.Sqlite;
using System.Text.Json;
using BGPLite.Api;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Tests;

/// <summary>
/// Regression coverage for #157: raw exception messages must NOT reach the client (EF Core /
/// SQLite / JSON internals — table names, constraint text, file paths — are reconnaissance
/// surface). <see cref="ManagementApi.MapExceptionToResponse"/> maps unhandled exceptions to a
/// stable, non-revealing client response with the correct status code.
/// </summary>
public class ExceptionMappingTests
{
    [Fact]
    public void JsonException_MapsTo_400_MalformedJsonBody()
    {
        // A malformed JSON body is the client's fault — 400, not 500.
        var (message, status) = ManagementApi.MapExceptionToResponse(new JsonException("Unexpected char at 0:50"));

        Assert.Equal(400, status);
        Assert.Equal("Malformed JSON body", message);
        // The raw JsonException detail must NOT appear in the client-facing message.
        Assert.DoesNotContain("Unexpected char", message);
    }

    [Fact]
    public void DbUpdateException_MapsTo_409_Conflict()
    {
        // A unique-constraint violation (concurrent duplicate CreatePeer/UpsertPeer) is a 409, not
        // a 500. EF Core wraps SQLite's "UNIQUE constraint failed" in DbUpdateException.
        var inner = new InvalidOperationException("UNIQUE constraint failed: Peers.Ip, Peers.Asn");
        var ex = new DbUpdateException("An error occurred while saving the entity changes.", inner);

        var (message, status) = ManagementApi.MapExceptionToResponse(ex);

        Assert.Equal(409, status);
        Assert.Equal("The resource already exists or conflicts with the current state", message);
        // The raw constraint text must NOT appear in the client-facing message.
        Assert.DoesNotContain("UNIQUE constraint", message);
        Assert.DoesNotContain("Peers.Ip", message);
    }

    [Fact]
    public void GenericException_MapsTo_500_GenericMessage()
    {
        // Anything else: generic message, full detail stays in the server log.
        var ex = new InvalidOperationException("Table 'Peers' has column 'Ip' at /data/db.sqlite:42");

        var (message, status) = ManagementApi.MapExceptionToResponse(ex);

        Assert.Equal(500, status);
        Assert.Equal("Internal server error", message);
        // File paths, table names, internals must NOT leak.
        Assert.DoesNotContain("/data/db.sqlite", message);
        Assert.DoesNotContain("Peers", message);
    }

    [Fact]
    public void NestedJsonException_StillMapsTo_400()
    {
        // If JsonException is wrapped (e.g. by a deserializer), the mapping catches it by type.
        var wrapped = new AggregateException(new JsonException("bad json"));
        // AggregateException is not JsonException itself — it falls through to 500. This pins that
        // the mapping checks the exact exception type (callers should let JsonException propagate
        // unwrapped, which the JSON deserializer does).
        var (message, status) = ManagementApi.MapExceptionToResponse(wrapped);
        Assert.Equal(500, status);
        Assert.Equal("Internal server error", message);
    }

    /// <summary>
    /// CodeRabbit #172 follow-up: cancellation is never an error. The HandleAsync catch-all filters
    /// OperationCanceledException out (via `when (ex is not OperationCanceledException)`), so it
    /// propagates to the host's cancellation handling instead of being mapped to a 500. This test
    /// documents the contract: MapExceptionToResponse itself would return 500 for an OCE (it does
    /// not special-case it), so the catch-site filter is what enforces "cancellation is not an error".
    /// </summary>
    [Fact]
    public void MapExceptionToResponse_DoesNotSpecialCaseCancellation_CatchSiteFilters_It()
    {
        // The mapping itself returns 500 for an OCE — the catch-site `when` filter is what prevents
        // it from ever being called with one. Pin this so the contract is explicit: callers MUST
        // filter OCE before calling MapExceptionToResponse.
        var (message, status) = ManagementApi.MapExceptionToResponse(new OperationCanceledException());
        Assert.Equal(500, status);
        Assert.Equal("Internal server error", message);
    }

    /// <summary>
    /// #266 item 8: an FK violation (SQLite code 19) after a concurrent DELETE removed the parent
    /// peer row must answer 404 "removed", not 409 "already exists" for a resource that is gone.
    /// </summary>
    [Fact]
    public void DbUpdateException_FkViolation_MapsTo_404()
    {
        var ex = new DbUpdateException("constraint failed", new SqliteException("FK constraint failed", 19, 787));
        var (message, status) = ManagementApi.MapExceptionToResponse(ex);

        Assert.Equal(404, status);
        Assert.Contains("removed", message);
    }

    [Fact]
    public void DbUpdateException_UniqueViolation_Still409()
    {
        // SQLITE_CONSTRAINT_UNIQUE — a concurrent duplicate insert remains a genuine conflict.
        // Realistic shape: BOTH classes report primary 19; the discriminator is the EXTENDED
        // code (2067 = SQLITE_CONSTRAINT_UNIQUE).
        var ex = new DbUpdateException("unique", new SqliteException("UNIQUE constraint failed: Peers.Ip", 19, 2067));
        var (message, status) = ManagementApi.MapExceptionToResponse(ex);

        Assert.Equal(409, status);
        Assert.Contains("exists", message);
    }
}
