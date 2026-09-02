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
           net bgp4  172.30.100.0/24        net bgp6  fd00:b00b::/64
  server   172.30.100.10                    fd00:b00b::10        (BGPLite :179, API 5001)
  bird     172.30.100.20                    fd00:b00b::20        (BIRD2, AS65002)
```

Server AS65001. Two BGP sessions: `bgplite4` (IPv4 transport) and `bgplite6`
(IPv6 transport — this one exercises the dual-mode listener from #14 phase 4a).
Both images are pinned to `platform: linux/amd64`; the server is published with
the `linux-x64` RID, so the stand is identical on an arm64 Mac (Rosetta) and the
amd64 CI runner.

## What is asserted

1. The management API answers (`GET /api/server`).
2. `POST /api/peers` registers the BIRD peer (custom prefix `192.0.2.0/24`).
3. Both sessions reach `Established` (checked from BIRD's view, `birdc`, and the
   server reports `active >= 2`).
4. BIRD → server propagation: BIRD's 3 static announcements (2× IPv4 NLRI, 1×
   IPv6 NLRI via MP_REACH) appear in the server's route table (`GET /api/routes`
   — the untagged `default` community group must hold exactly 3).
5. Server → BIRD propagation: the seeded prefixes `10.100.0.0/16`,
   `10.200.0.0/16` (file source, `test-nets.txt`) are present in BIRD's BGP
   import on the IPv4 session.

## Run it

```bash
docker/integration/run-tests.sh
```

Requirements: `docker` with the compose v2 plugin (daemon running), `curl`.
The script builds, starts, polls until assertions pass, and always tears the
stand down (`docker compose down -v`). Exit code is the test verdict.

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
