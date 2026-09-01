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
LIMIT_CM=${LIMIT_CM:-200}

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

# Every container, not just the server. Bots outlive it -- the server closing the
# session does not stop them, they run to their own frame count -- and reading
# docker logs from a live container returns whatever stdout happens to have been
# flushed, which for a block-buffered process is often nothing at all. Earlier
# runs of this script collected truncated bot logs and reported error counts that
# moved between identical runs.
docker wait wb-srv >/dev/null
for i in $(seq 1 "$CLIENTS"); do docker wait "wb-bot$i" >/dev/null; done

# Cleared, not appended to. A four-bot run used to leave bot4.log behind for the
# next three-bot run to read as its own result.
rm -rf /tmp/lagtest && mkdir -p /tmp/lagtest
docker logs wb-srv > /tmp/lagtest/server.log 2>&1
for i in $(seq 1 "$CLIENTS"); do docker logs "wb-bot$i" > "/tmp/lagtest/bot$i.log" 2>&1; done

# Errors that name our own code, as opposed to Godot disconnecting signals it has
# already disconnected during scene teardown. The latter is known, is cosmetic,
# and varies run to run; the former is a bug and must be zero.
ours() {
  awk '
    /^ERROR/ { if (block && hit) n++; block=1; hit=0; win=0; next }
    block    { win++; if (/Wipebound\./) hit=1; if (win >= 8) { if (hit) n++; block=0 } }
    END      { if (block && hit) n++; print n+0 }
  ' "$1"
}

# Every extraction ends in || true: pipefail is on, and a grep that finds
# nothing is a legitimate outcome this script must report rather than die on.
OVERREACH=$(grep -oE '"worst_overreach_cm":[0-9]+' /tmp/lagtest/server.log | grep -oE '[0-9]+$' | sort -rn | head -1 || true)
OVERREACH=${OVERREACH:-missing}
# Casts the server RESOLVED for a hero, which is the thing that has to survive
# a bad network. Not damage dealt: a headless bot aims where its cursor is and
# its cursor is not the boss, so it can play perfectly and still hit nothing.
CASTS=$(grep -cE '^\[resolve\] hero' /tmp/lagtest/server.log || true)
CASTS=${CASTS:-0}
RTT=$(grep -h '\[bot\] rtt' /tmp/lagtest/bot*.log | awk '{print $3}' | sort -u | tr '\n' ' ' || true)

echo
echo "shaping:   $(grep -h '\[lag\]' /tmp/lagtest/*.log | sort -u || true)"
echo "rtt seen:  ${RTT:-NONE}"
echo "overreach: ${OVERREACH}cm (ranked limit ${LIMIT_CM}cm)"
echo "bot casts:  ${CASTS}"

FAIL=0

# The bots must have actually PLAYED. They once pressed and released an ability
# inside a single frame, so the physics tick that reads input never saw it and
# every bot cast nothing for an entire run -- while this script still reported a
# clean result, because it printed numbers and never judged them.
if [ "$CASTS" -le 0 ]; then
  echo "FAIL: no hero cast resolved, so nothing here tested casting under latency"
  FAIL=1
fi

if [ -z "$RTT" ]; then
  echo "FAIL: no bot ever reported a round trip, so the run proves nothing"
  FAIL=1
fi

if [ "$OVERREACH" = "missing" ]; then
  echo "FAIL: no run was recorded, so no overreach was measured -- give it more frames"
  FAIL=1
elif [ "$OVERREACH" -gt "$LIMIT_CM" ]; then
  echo "FAIL: honest bots were billed ${OVERREACH}cm of overreach, over the ${LIMIT_CM}cm ranked limit"
  FAIL=1
fi

for f in /tmp/lagtest/server.log /tmp/lagtest/bot*.log; do
  mine=$(ours "$f")
  total=$(grep -cE '^ERROR|^SCRIPT ERROR' "$f" || true)
  echo "$(basename "$f"): $mine error(s) in our code, $total engine lines total"
  if [ "$mine" -gt 0 ]; then
    echo "FAIL: $(basename "$f") raised errors from Wipebound code"
    grep -A6 -E '^ERROR' "$f" | grep -B4 'Wipebound\.' | head -12
    FAIL=1
  fi
done

echo
if [ "$FAIL" -ne 0 ]; then
  echo "== FAILED =="
  exit 1
fi

echo "== PASSED: honest play at ~$((LAG_MS * 2))ms rtt, ${LOSS_PCT}% loss, is not accused of anything =="
