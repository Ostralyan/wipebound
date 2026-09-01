#!/bin/bash
# Shape this container's OUTBOUND traffic, then run the game.
#
# Applied to every container, server and clients alike, so each direction of
# every conversation is delayed once and the round trip is the sum of the two.
# Delaying only the server would have produced an asymmetric path, and an
# NTP-style clock like NetClock's estimates the offset wrong by half of any
# asymmetry -- which would have shown up as a clock bug that was really a
# harness bug.
set -e

if [ -n "${LAG_MS:-}" ]; then
  tc qdisc add dev eth0 root netem \
     delay "${LAG_MS}ms" "${JITTER_MS:-0}ms" distribution normal \
     loss "${LOSS_PCT:-0}%"
  echo "[lag] eth0 shaped: +${LAG_MS}ms jitter ${JITTER_MS:-0}ms loss ${LOSS_PCT:-0}%"
fi

exec /usr/local/bin/wipebound --headless --quit-after "${FRAMES:-9000}" -- "$@"
