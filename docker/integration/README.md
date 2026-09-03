# Integration tests: BGPLite × BIRD2 (docker compose, linux64)

End-to-end BGP tests against REAL speaker processes — no mocks. A BGPLite route
server container and a [BIRD2](https://bird.network.cz/) peer container exchange
routes over **both** transport families, and a shell script asserts the result
through BGPLite's management API and `birdc`.

BIRD2 was chosen over alternatives (e.g. MikroTik CHR) because it is a small
deterministic container, starts in milliseconds, needs no license/bootstrap, and
its `birdc` CLI gives precise per-protocol assertions.

## Topology

```
           net bgp4  172.30.100.0/24        net bgp6  2001:db8:cccc::/64
  server   172.30.100.10                    2001:db8:cccc::10    (BGPLite :179, API 5001)
  bird     172.30.100.20                    2001:db8:cccc::20    (BIRD2, AS65002)
```

Server AS65001. Two BGP sessions: `bgplite4` (IPv4 transport) and `bgplite6`
(IPv6 transport — this one exercises the dual-mode listener from #14 phase 4a).
Both images are pinned to `platform: linux/amd64`; the server is published with
the `linux-x64` RID, so the stand is identical on an arm64 Mac (Rosetta) and the
amd64 CI runner.

## What is asserted

1. The management API answers (`GET /api/server`).
2. `POST /api/peers` registers the BIRD peer with the `openai` subscription
   (an HTTP prefix source fed from [ruhex/prefix-lists](https://github.com/ruhex/prefix-lists)
   raw data — the full HTTP-fetch pipeline runs against real-world lists) plus a
   custom prefix `192.0.2.0/24`.
3. Both sessions reach `Established` (checked from BIRD's view, `birdc`, and the
   server reports `active >= 2`).
4. BIRD → server propagation: BIRD's 3 static announcements (2× IPv4 NLRI, 1×
   IPv6 NLRI via MP_REACH) appear in the server's route table (`GET /api/routes`
   — the untagged `default` community group must hold exactly 3).
5. Server → BIRD propagation: the custom prefix (192.0.2.0/24) plus >= 100
   routes from the `openai` HTTP source on the IPv4 session, and the seed
   prefixes `10.100.0.0/16`, `10.200.0.0/16` (file source, `test-nets.txt`)
   riding as classic IPv4 NLRI over the IPv6-transport session.

## Run it

```bash
docker/integration/run-tests.sh              # core suite
docker/integration/run-tests.sh --capture    # + packet capture for analysis
```

Requirements: `docker` with the compose v2 plugin (daemon running), `curl`,
`tcpdump` on the host for `--capture` verification. The script builds, starts,
polls until assertions pass, and always tears the stand down
(`docker compose down -v`). Exit code is the test verdict.

API assertions go through an in-network `probe` sidecar (curlimages/curl) —
no host port publishing is required. For interactive poking, `exec` into the
probe: `docker compose -f docker/integration/docker-compose.yml exec probe
curl http://server:5001/api/server`.

**Traffic capture (`--capture`)**: a tcpdump sidecar joins the server's network
namespace and records everything on port 179 (both transports) to
`docker/integration/captures/bgp.pcap` — open it in Wireshark (filter `bgp`)
to inspect OPEN/UPDATE/KEEPALIVE exchange. The runner verifies the pcap holds
a real BGP conversation before passing.

The same script runs in CI: `.github/workflows/integration.yml` (push to
`main`/`dev` + manual `workflow_dispatch`).

## Files

| File | Purpose |
|---|---|
| `docker-compose.yml` | The stand: two networks, fixed addresses, linux/amd64 |
| `server-appsettings.yml` | Offline server config (file prefix source, short timers) |
| `test-nets.txt` | Seed prefixes the server originates |
| `bird.conf` | BIRD2 config: two BGP protocols + static announcements |
| `Dockerfile.bird` | BIRD2 image (Debian bookworm package) |
| `run-tests.sh` | The test runner |
