#!/usr/bin/env bash
# Watch the bots play, with a window.
#
# Nothing here is special to bots. A client renders every hero the server
# replicates to it, so bots stay headless and cost nothing to look at -- what
# opens a window is you, joining the same server they did.
#
#   tools/watch.sh            five bots, you join and play alongside them
#   tools/watch.sh 8 observe  eight bots, you watch from above with no hero
#
# observe runs the SERVER windowed. A dedicated server simulates the whole fight
# and never gets a hero, so its camera has nothing to follow and sits looking at
# the arena: arrow keys pan, the wheel zooms, and you cannot accidentally play.
set -euo pipefail

BOTS=${1:-5}
MODE=${2:-play}
PORT=${PORT:-7777}
GODOT=${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}
HERE="$(cd "$(dirname "$0")/.." && pwd)"

NAMES=(Kestrel Bramble Cinder Thorn Willow Ash Fern Rook)

pkill -f "Godot.*--port $PORT" 2>/dev/null || true
sleep 1

if [ "$MODE" = "observe" ]; then
  echo "== server window is your view; $BOTS bots joining =="
  "$GODOT" --path "$HERE" -- --server --port "$PORT" &
else
  echo "== headless server, $BOTS bots, and a window for you =="
  "$GODOT" --headless --path "$HERE" -- --server --port "$PORT" >/dev/null 2>&1 &
fi

# Let the server bind before anybody knocks.
sleep 4

for i in $(seq 0 $((BOTS - 1))); do
  "$GODOT" --headless --path "$HERE" -- \
    --join 127.0.0.1 --port "$PORT" --bot \
    --name "${NAMES[$((i % 8))]}" --class $((i % 3)) >/dev/null 2>&1 &
  sleep 0.4
done

if [ "$MODE" != "observe" ]; then
  sleep 2
  echo "== joining as you =="
  "$GODOT" --path "$HERE" -- --join 127.0.0.1 --port "$PORT" --name "$(whoami)" --class 0 &
fi

cat <<'KEYS'

  WASD          move            LMB RMB Q E R F   rotational abilities
  mouse         aim             1 2 3             situational
  Tab           meter mode      Space C           defensive
  arrows        pan camera      X                 ultimate
  Home          recentre        wheel             zoom

  Ctrl-C here stops everything.
KEYS

trap 'pkill -f "Godot.*--port '"$PORT"'" 2>/dev/null || true' EXIT
wait
