using BGPLite.Contracts;
using BGPLite.Providers;

namespace BGPLite.Tests.Integration;

/// <summary>
/// Recording ISessionManager for hosted-service tests: counts RefreshAllEstablishedAsync calls and
/// captures SetPeerMd5Key arming — enough to assert the service under test drove the right manager
/// operations without a live BgpServer (and without Linux-only TCP-MD5 kernel arming).
/// </summary>
public sealed class FakeSessionManager : ISessionManager
{
    public int RefreshAllCount;
    public int RefreshPeerCount;
    public List<(string Ip, string? Password)> Md5Keys { get; } = [];

    public Task RefreshPeerAsync(string peerIp, uint asn)
    {
        RefreshPeerCount++;
        return Task.CompletedTask;
    }

    public List<string> GetActivePeerIps() => [];

    public bool IsPeerEstablished(string peerIp, uint asn) => false;

    public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;

    public Task RefreshAllEstablishedAsync()
    {
        RefreshAllCount++;
        return Task.CompletedTask;
    }

    public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;

    public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;

    public void SetPeerMd5Key(string peerIp, string? password) => Md5Keys.Add((peerIp, password));

    public bool IsPeerMd5Armed(string peerIp, string? password) => false;
}

/// <summary>Counting IPrefixSourceService for the auto-refresh timer test.</summary>
public sealed class FakePrefixSourceService : IPrefixSourceService
{
    public int RefreshCalls;
    public bool NextRefreshResult;

    public event Action<string>? ContentCommitted;

    public Task<IReadOnlyList<BGPLite.Protocol.IpPrefix>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BGPLite.Protocol.IpPrefix>>([]);

    public Task<(IReadOnlyList<BGPLite.Protocol.IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<BGPLite.Protocol.IpPrefix>, bool)>(([], false));

    public Task<IReadOnlyList<(BGPLite.Configuration.PrefixSourceConfig Source, IReadOnlyList<BGPLite.Protocol.IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(BGPLite.Configuration.PrefixSourceConfig, IReadOnlyList<BGPLite.Protocol.IpPrefix>)>>([]);

    public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default)
    {
        RefreshCalls++;
        await Task.Yield();
        return NextRefreshResult;
    }

    public bool SourceSupportsConditional(string sourceName) => true;
}

