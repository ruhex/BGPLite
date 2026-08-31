using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #318: the OPEN BGPLite sends must NOT carry the Graceful Restart capability (64). The
/// receiving-speaker half of RFC 4724 §4.2 (retain + stale-mark a restarting peer's routes) is
/// not implemented, so advertising the &lt;AFI, SAFI, F&gt; tuple promised behavior the code does
/// not have (D6).
/// </summary>
public class BgpSessionOpenCapabilityTests
{
    [Fact]
    public async Task SentOpen_DoesNotAdvertise_GracefulRestart()
    {
        var conn = new ScriptedConnection();
        var config = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        // The default configuration is the exact one that used to advertise the capability.
        Assert.True(config.GracefulRestart);

        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            config,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance);

        var run = session.RunAsync();
        try
        {
            conn.EnqueueMessage(new BgpOpenMessage
            {
                Version = BgpConstants.BgpVersion,
                Asn = 65002,
                HoldTime = 0,
                RouterId = 0x0A000002,
                Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
            });
            conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

            for (var i = 0; i < 200 && !session.IsEstablished; i++)
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            Assert.True(session.IsEstablished, "session must reach Established");

            // The OPEN we send is the session's first outbound frame.
            var sent = conn.Sent;
            Assert.True(sent.Count > 0, "session must have sent its OPEN");
            var open = Assert.IsType<BgpOpenMessage>(BgpMessageReader.ReadMessage(sent[0]));

            // RED pre-fix: the GR capability (code 64) rode along with the 4-octet-AS set.
            Assert.DoesNotContain(open.Capabilities, c => c.Code == BgpConstants.Capability.GracefulRestart);
            // The 4-octet ASN capability — advertised unconditionally — is untouched.
            Assert.Contains(open.Capabilities, c => c.Code == BgpConstants.Capability.FourOctetAsn);
        }
        finally
        {
            session.MarkSilentClose();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            session.Dispose();
        }
    }
}
