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

API="http://localhost:15001"
API_PORT_HOST=15001
COMPOSE="docker compose -f docker-compose.yml"

say() { printf '\n=== %s ===\n' "$1"; }
fail() { printf 'FAILED: %s\n' "$1" >&2; exit 1; }

# --- bring the stand up -------------------------------------------------------
say "build (linux/amd64)"
$COMPOSE build --pull

say "up"
$COMPOSE up -d
cleanup() { $COMPOSE down -v --remove-orphans; }
trap cleanup EXIT

# --- helpers ------------------------------------------------------------------
api_get() { curl -sf --max-time 5 "$API$1"; }
TMP=$(mktemp -d)
birdc() { $COMPOSE exec -T bird birdc "$@"; }

# Wait until `bash -c "$1"` succeeds; $2 = deadline in seconds.
wait_for() {
  local what="$1" deadline="$2" i=0
  while ! eval "$what"; do
    i=$((i + 1))
    [ "$i" -gt "$deadline" ] && fail "timeout after ${deadline}s waiting for: $what"
    sleep 1
  done
  printf 'ok (%ss): %s\n' "$i" "$what"
}

# --- 1. API plane --------------------------------------------------------------
say "1. API reachable"
wait_for 'api_get /api/server >/dev/null' 60

# --- 2. register the IPv4 peer --------------------------------------------------
say "2. register peer 172.30.100.20 (AS65002, custom prefix 192.0.2.0/24)"
PEER_BODY='{"ip":"172.30.100.20","asn":65002,"description":"integration-bird","customPrefixes":["192.0.2.0/24"]}'
CREATE_STATUS=$(curl -s --max-time 10 -o "$TMP/create-peer.json" -w '%{http_code}' \
  -X POST -H 'Content-Type: application/json' -d "$PEER_BODY" "$API/api/peers") \
  || fail "POST /api/peers curl error"
[ "$CREATE_STATUS" = "200" ] || fail "POST /api/peers returned $CREATE_STATUS: $(cat "$TMP/create-peer.json")"
grep -q '"error"' "$TMP/create-peer.json" && fail "POST /api/peers rejected: $(cat "$TMP/create-peer.json")"
echo "created: $(cat "$TMP/create-peer.json")"

# --- 3. both sessions Established ----------------------------------------------
# birdc "show protocols all <name>" prints "BGP state: Established" once established.
say "3. BGP sessions Established (IPv4 + IPv6 transport)"
wait_for 'birdc "show protocols all bgplite4" 2>/dev/null | grep -q "BGP state:.*Established"' 90
wait_for 'birdc "show protocols all bgplite6" 2>/dev/null | grep -q "BGP state:.*Established"' 90

ACTIVE=$(api_get /api/sessions | grep -o '"active":[0-9]*' | cut -d: -f2)
[ -n "$ACTIVE" ] && [ "$ACTIVE" -ge 2 ] || fail "server reports active=$ACTIVE, want >=2 (both transports)"
printf 'server reports %s active sessions\n' "$ACTIVE"

# --- 4. BIRD -> server route propagation ----------------------------------------
say "4. BIRD announcements reach the server route table"
# Seed prefixes carry community 65001:100; BIRD's are untagged -> the "default"
# group must grow to exactly the 3 announced prefixes (2x IPv4 NLRI + 1x MP_REACH v6).
wait_for 'api_get /api/routes | grep -q "\"default\":3"' 60
ROUTES=$(api_get /api/routes)
echo "$ROUTES"
echo "$ROUTES" | grep -q '"total":5' || fail "route table should hold 2 seed + 3 BIRD routes: $ROUTES"

# --- 5. server -> BIRD route propagation ----------------------------------------
say "5. server advertisements reach BIRD"
# The REGISTERED IPv4 peer gets its custom prefix (the configured-peer pipeline:
# custom prefixes + subscriptions, no RU defaults).
wait_for 'birdc "show route protocol bgplite4" 2>/dev/null | grep -q "192.0.2.0/24"' 60
birdc "show route protocol bgplite4" || true

# The UNREGISTERED IPv6-transport peer gets the RU defaults (= the file seed source).
# They ride as classic IPv4 NLRI over the IPv6 transport, imported by bgplite6's
# IPv4 channel.
wait_for 'birdc "show route protocol bgplite6" 2>/dev/null | grep -q "10.100.0.0/16"' 60
wait_for 'birdc "show route protocol bgplite6" 2>/dev/null | grep -q "10.200.0.0/16"' 60
birdc "show route protocol bgplite6" || true

say "ALL INTEGRATION TESTS PASSED"
