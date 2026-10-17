using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// <c>BgpServer.Dispose()</c> must be idempotent. The composition root registers the one instance
/// under three descriptors — the <c>BgpServer</c> singleton, the hosted service, and
/// <c>ISessionManager</c> — and <c>ServiceProviderEngineScope.CaptureDisposable</c> does not
/// de-duplicate, so MS.DI captures it once per descriptor and disposes it that many times.
/// <c>StopAsync</c> cancels but never disposes the CTS, so the first disposal always succeeded and
/// the second threw <see cref="ObjectDisposedException"/> out of <c>_cts.Cancel()</c> — which the
/// provider's reverse walk does not catch, so every disposable captured EARLIER was skipped and the
/// exception escaped <c>host.RunAsync()</c>. The symptom was a stack trace and a non-zero exit on
/// every clean shutdown (SIGTERM in Docker, Ctrl-C locally), with the logging stack torn down
/// abruptly and its buffered shutdown logs lost. The sibling class got the same guard in #141.
/// </summary>
public class BgpServerDisposeTests
{
    private static BgpServer NewServer() => new(
        new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
        new RouteTable(),
        AllowAllFilter.Instance,
        new BgpMetrics(),
        new NeverReachedSessionFactory(),
        NullLogger<BgpServer>.Instance);

    /// <summary>The guard itself: a second (and third) Dispose is a no-op, not a double-release.</summary>
    [Fact]
    public void Dispose_CalledRepeatedly_IsANoOp()
    {
        var server = NewServer();

        server.Dispose();
        server.Dispose();   // threw ObjectDisposedException from _cts.Cancel() before the guard
        server.Dispose();
    }

    /// <summary>
    /// The production trigger, not just the guard: the real registration shape from
    /// <c>Program.cs</c> (singleton + hosted service + <c>ISessionManager</c>, all resolving the
    /// one instance) disposed through the container. The assertion is on the DISPOSAL CHAIN, which
    /// is what the shutdown actually lost — <c>earlier</c> is captured before the server, so the
    /// provider disposes it last, and a throw from the second <c>BgpServer</c> disposal means it
    /// never runs at all.
    /// </summary>
    [Fact]
    public void ServiceProviderDispose_WithTheProductionRegistrationShape_CompletesTheChain()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TrackedDisposable>();
        services.AddSingleton(_ => NewServer());
        services.AddHostedService(sp => sp.GetRequiredService<BgpServer>());
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<BgpServer>());

        var provider = services.BuildServiceProvider();

        // Resolution order IS capture order, and the provider disposes in reverse: resolve the
        // tracker first so it sits at the bottom of the chain, then realize all three server
        // descriptors so the instance is captured three times over.
        var earlier = provider.GetRequiredService<TrackedDisposable>();
        var server = provider.GetRequiredService<BgpServer>();
        Assert.Same(server, provider.GetServices<IHostedService>().OfType<BgpServer>().Single());
        Assert.Same(server, provider.GetRequiredService<ISessionManager>());

        provider.Dispose();

        Assert.True(earlier.Disposed,
            "the provider's disposal chain aborted: a second BgpServer.Dispose threw and skipped every disposable captured earlier");
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>These tests never start the listener, so no session is ever created.</summary>
    private sealed class NeverReachedSessionFactory : IBgpSessionFactory
    {
        public BgpSession Create(IBgpConnection connection, PeerConfig peerConfig)
            => throw new InvalidOperationException("session factory must not be reached in this test");
    }
}
