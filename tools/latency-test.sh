#!/usr/bin/env bash
# Play the game over a deliberately bad network and see what the server accuses
# honest players of.
#
# Every part of this project's netcode has only ever run at loopback latency,
# where the round trip is effectively zero. The movement validator's grace window
# is DERIVED from round-trip time, so at zero it has been getting a number it
# will never see in production.
#
# Server and bots are all shaped, so each direction is delayed once and the round
# trip is the sum. Shaping only the server would make the path asymmetric, and an
# NTP-style clock estimates its offset wrong by half of any asymmetry -- a harness
# artefact that would read exactly like a clock bug.
#
#   tools/latency-test.sh [one-way-ms] [jitter-ms] [loss-%] [clients] [frames]
set -euo pipefail

LAG_MS=${1:-40}
JITTER_MS=${2:-10}
LOSS_PCT=${3:-1}
CLIENTS=${4:-3}
FRAMES=${5:-9000}

NET=wipebound-lagnet
cleanup() {
  docker rm -f wb-srv $(for i in $(seq 1 "$CLIENTS"); do echo -n "wb-bot$i "; done) >/dev/null 2>&1 || true
  docker network rm $NET >/dev/null 2>&1 || true
}
trap cleanup EXIT
cleanup

docker network create $NET >/dev/null

echo "== shaping: ${LAG_MS}ms each way (~$((LAG_MS * 2))ms rtt), ${JITTER_MS}ms jitter, ${LOSS_PCT}% loss, ${CLIENTS} bots =="

docker run -d --name wb-srv --network $NET --cap-add=NET_ADMIN \
  -e LAG_MS="$LAG_MS" -e JITTER_MS="$JITTER_MS" -e LOSS_PCT="$LOSS_PCT" -e FRAMES="$FRAMES" \
  wipebound-lag --server --port 7777 >/dev/null

sleep 6
IP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' wb-srv)
echo "== server at $IP =="

for i in $(seq 1 "$CLIENTS"); do
  docker run -d --name "wb-bot$i" --network $NET --cap-add=NET_ADMIN \
    -e LAG_MS="$LAG_MS" -e JITTER_MS="$JITTER_MS" -e LOSS_PCT="$LOSS_PCT" \
    -e FRAMES="$((FRAMES - 900))" \
    wipebound-lag --join "$IP" --port 7777 --bot --class "$(((i - 1) % 3))" >/dev/null
done

echo "== running =="
docker wait wb-srv >/dev/null

mkdir -p /tmp/lagtest
docker logs wb-srv > /tmp/lagtest/server.log 2>&1
for i in $(seq 1 "$CLIENTS"); do docker logs "wb-bot$i" > "/tmp/lagtest/bot$i.log" 2>&1; done

echo
echo "== shaping actually applied =="
grep -h '\[lag\]' /tmp/lagtest/*.log | sort -u

echo
echo "== round trip the game observed =="
grep -h '\[bot\] rtt' /tmp/lagtest/bot*.log | awk '{print $3}' | sort -u | tr '\n' ' '; echo

echo
echo "== overreach billed to honest bots (ranked limit is 200cm) =="
grep -oE '"overreach_cm":[0-9]+|"worst_overreach_cm":[0-9]+' /tmp/lagtest/server.log | sort -u

echo
echo "== engine errors =="
echo "server: $(grep -cE '^ERROR|^SCRIPT ERROR' /tmp/lagtest/server.log || true)"
for i in $(seq 1 "$CLIENTS"); do
  echo "bot$i:   $(grep -cE '^ERROR|^SCRIPT ERROR' "/tmp/lagtest/bot$i.log" || true)"
done
