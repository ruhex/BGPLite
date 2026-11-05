namespace BGPLite.Contracts;

/// <summary>A complete canonical peer-prefix projection, or an explicit degraded marker.</summary>
public sealed record PrefixExportResult(IReadOnlyList<string> Prefixes, bool Complete);

/// <summary>Projects the same outbound route set used by BGP without exposing Server types to Api.</summary>
public interface IPrefixExportService
{
    Task<PrefixExportResult> ExportAsync(string peerIp, uint remoteAsn, CancellationToken ct = default);
}
