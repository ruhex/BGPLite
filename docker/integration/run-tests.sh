#!/usr/bin/env bash
# BGPLite × BIRD2 integration tests (docker compose, linux/amd64).
#
# What is proven against REAL BGP speaker processes (no mocks):
#   1. API plane is reachable (/api/server).
#   2. A configured peer (registered via POST /api/peers) establishes over IPv4 transport.
#   3. An UNconfigured peer establishes over IPv6 transport — proves the dual-mode
#      listener (#14 phase 4a): fd00:b00b::20 -> fd00:b00b::10 port 179.
#   4. Route exchange, server <- BIRD: BIRD's static prefixes (203.0.113.0/24,
#      198.51.100.0/24 via IPv4 session, 2001:db8:ffff::/48 via MP_REACH over the
#      IPv6 session) land in the server's route table (the "default" community group
#      in /api/routes grows by 3 — announced prefixes carry no community).
#   5. Route exchange, server -> BIRD: the seed prefixes from the file source
#      (10.100.0.0/16, 10.200.0.0/16) appear in BIRD's import for the IPv4 session.
#
# Requirements: docker + docker compose (v2), curl. The runner itself may be any OS.
set -euo pipefail
cd "$(dirname "$0")"

# The API is reached THROUGH the probe sidecar (compose network, by service name):
# host-loopback port publishing is unavailable under some CI docker configurations
# (userland-proxy/iptables variance) — this way the tests depend only on the
# compose network itself.
API="http://server:5001"
CAPTURE=0
[ "${1:-}" = "--capture" ] && CAPTURE=1
PROFILES=""
[ "$CAPTURE" = 1 ] && PROFILES="--profile capture"
COMPOSE="docker compose $PROFILES -f docker-compose.yml"

say() { printf '\n=== %s ===\n' "$1"; }
fail() { printf 'FAILED: %s\n' "$1" >&2; exit 1; }

api_get() { $COMPOSE exec -T probe curl -sf --max-time 5 "$1"; }
api_post() { $COMPOSE exec -T probe curl -s --max-time 10 -o - -w '\n%{http_code}' -X POST -H 'Content-Type: application/json' -d "$2" "$1"; }

# --- bring the stand up -------------------------------------------------------
say "build (linux/amd64)"
$COMPOSE build --pull

say "up"
$COMPOSE up -d
cleanup() {
  # Evidence before teardown: the first thing a CI failure needs is the server's
  # own account of why the API/session assertions never succeeded.
  say "server log (tail, pre-teardown)"
  $COMPOSE logs --no-color --tail 120 server bird 2>&1 | tail -120 || true
  $COMPOSE ps -a || true
  $COMPOSE down -v --remove-orphans
}
trap cleanup EXIT

# --- helpers ------------------------------------------------------------------
TMP=$(mktemp -d)
birdc() { $COMPOSE exec -T bird birdc "$@"; }

# Wait until `bash -c "$1"` succeeds; $2 = deadline in seconds.
wait_for() {
  local what="$1" deadline="$2" i=0
  while ! eval "$what"; do
    i=$((i + 1))
    if [ "$i" -gt "$deadline" ]; then
      $COMPOSE ps -a || true
      fail "timeout after ${deadline}s waiting for: $what"
    fi
    sleep 1
  done
  printf 'ok (%ss): %s\n' "$i" "$what"
}

# --- 1. API plane --------------------------------------------------------------
say "1. API reachable"
wait_for 'api_get $API/api/server >/dev/null' 60

# --- 2. register the IPv4 peer --------------------------------------------------
say "2. register peer 172.30.100.20 (AS65002, custom prefix 192.0.2.0/24)"
PEER_BODY='{"ip":"172.30.100.20","asn":65002,"description":"integration-bird","lists":["openai"],"customPrefixes":["192.0.2.0/24"]}'
CREATE_RESP=$(api_post "$API/api/peers" "$PEER_BODY") || fail "POST /api/peers curl error"
CREATE_STATUS=$(echo "$CREATE_RESP" | tail -1)
echo "$CREATE_RESP" | sed '$d' > "$TMP/create-peer.json"
[ "$CREATE_STATUS" = "200" ] || fail "POST /api/peers returned $CREATE_STATUS: $(cat "$TMP/create-peer.json")"
grep -q '"error"' "$TMP/create-peer.json" && fail "POST /api/peers rejected: $(cat "$TMP/create-peer.json")"
echo "created: $(cat "$TMP/create-peer.json")"

# --- 3. both sessions Established ----------------------------------------------
# BIRD connects the moment the stand is up, which races the peer registration in
# step 2 (the session's initial dump would run against a not-yet-created peer row
# and take the unconfigured path). Restarting the protocols forces a fresh dump
# that sees the registered peer — deterministic, no sleep-based hope.
say "3. restart BIRD sessions, then Established (IPv4 + IPv6 transport)"
birdc "restart bgplite4" >/dev/null 2>&1
birdc "restart bgplite6" >/dev/null 2>&1
wait_for 'birdc "show protocols all bgplite4" 2>/dev/null | grep -q "BGP state:.*Established"' 90
wait_for 'birdc "show protocols all bgplite6" 2>/dev/null | grep -q "BGP state:.*Established"' 90

# Poll instead of a one-shot pipe: under `set -o pipefail` a single curl/grep hiccup
# here would silently kill the script (set -e) before any diagnostic is printed.
wait_for 'api_get $API/api/sessions | grep -q "\"active\":[2-9]"' 60
printf 'server reports both transports active\n'

# --- 4. BIRD -> server route propagation ----------------------------------------
say "4. BIRD announcements reach the server route table"
# Seed prefixes carry community 65001:100; BIRD's are untagged -> the "default"
# group must grow to exactly the 3 announced prefixes (2x IPv4 NLRI + 1x MP_REACH v6).
wait_for 'api_get $API/api/routes | grep -q "\"default\":3"' 60
ROUTES=$(api_get $API/api/routes)
echo "$ROUTES"
echo "$ROUTES" | grep -q '"65001:100":2' || fail "seed source group (65001:100) should hold 2: $ROUTES"
# The openai HTTP-source group must hold a healthy bulk (the live list has ~300;
# aggregation may merge some, lists may evolve — assert a conservative floor).
HTTP_ROUTES=$(echo "$ROUTES" | grep -oE '"65001:110":[0-9]+' | grep -oE '[0-9]+$')
[ -n "$HTTP_ROUTES" ] && [ "$HTTP_ROUTES" -ge 100 ] \
  || fail "openai HTTP-source group should hold >=100 routes, got: $HTTP_ROUTES"

# --- 5. server -> BIRD route propagation ----------------------------------------
say "5. server advertisements reach BIRD"
# The REGISTERED IPv4 peer gets its custom prefix (the configured-peer pipeline:
# custom prefixes + subscriptions, no RU defaults).
wait_for 'birdc "show route protocol bgplite4" 2>/dev/null | grep -q "192.0.2.0/24"' 60
birdc "show route protocol bgplite4" || true

# The peer also subscribes to the "openai" HTTP source (ruhex/prefix-lists): the full
# HTTP-fetch pipeline must deliver its ~300 real-world prefixes to BIRD. Assert a
# conservative floor (>= 100 routes) — aggregation may merge some, lists may evolve.
# (tail -n +2 skips the "BIRD 2.0.12 ready" banner — its digits would break the parse.)
ROUTES4=$(birdc "show route protocol bgplite4 count" 2>/dev/null | tail -n +2 | grep -oE "[0-9]+" | head -1)
[ -n "$ROUTES4" ] && [ "$ROUTES4" -ge 100 ] \
  || fail "expected >=100 routes from the openai HTTP source on bgplite4, got: $ROUTES4"
printf 'bgplite4 carries %s routes (custom + HTTP-source subscription)\n' "$ROUTES4"

# The UNREGISTERED IPv6-transport peer gets the RU defaults (= the file seed source).
# They ride as classic IPv4 NLRI over the IPv6 transport, imported by bgplite6's
# IPv4 channel.
wait_for 'birdc "show route protocol bgplite6" 2>/dev/null | grep -q "10.100.0.0/16"' 60
wait_for 'birdc "show route protocol bgplite6" 2>/dev/null | grep -q "10.200.0.0/16"' 60
birdc "show route protocol bgplite6" || true

# --- 6. traffic capture (opt-in: --capture) --------------------------------------
if [ "$CAPTURE" = 1 ]; then
  say "6. packet capture (--capture)"
  $COMPOSE stop capture
  PCAP=$(ls -S captures/*.pcap 2>/dev/null | head -1)
  [ -n "$PCAP" ] && [ -s "$PCAP" ] || fail "capture produced no pcap in captures/"
  BGP_PKTS=$(tcpdump -r "$PCAP" 2>/dev/null | grep -c "BGP" || true)
  [ -n "$BGP_PKTS" ] && [ "$BGP_PKTS" -ge 10 ] \
    || fail "pcap holds $BGP_PKTS BGP packets, expected >= 10: $PCAP"
  printf '%s: %s BGP packets captured (open in Wireshark for analysis)\n' "$PCAP" "$BGP_PKTS"
fi

say "ALL INTEGRATION TESTS PASSED"
