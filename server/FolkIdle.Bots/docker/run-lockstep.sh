#!/bin/sh
# Lockstep bot run INSIDE Docker, next to Postgres (folk-idle-db on the
# idlehra_default network). Windows -> Docker costs ~0.43 ms per database
# round trip, and one loot transaction makes 15-20 of them; on the Docker
# network it is a fraction of that.
#
# Usage (inside mcr.microsoft.com/dotnet/aspnet:8.0, /work = this folder):
#   run-lockstep.sh DB MIX HOURS OUT CRON_SCALE [SPEED_MODE]
# SPEED_MODE "lockstep" (default) or a number = paced FOLKIDLE_TICK_SPEED.
set -e
DB="$1"; MIX="$2"; HOURS="$3"; OUT="$4"; SCALE="${5:-1}"; MODE="${6:-lockstep}"
export FOLKIDLE_DB_CONN="Host=folk-idle-db;Database=$DB;Username=postgres;Password=postgres"
export REDIS_CONNECTION="folk-idle-redis:6379"
export FOLKIDLE_DEV_TOOLS=1 FOLKIDLE_BOSS_MINIGAME=wheel FOLKIDLE_OFFLINE_SIM=1
mkdir -p "$OUT"
cd /work/server
dotnet FolkIdle.Server.dll --migrate > "$OUT/migrate.log" 2>&1
if [ "$MODE" = "lockstep" ]; then
  export FOLKIDLE_SIM_LOCKSTEP=1 FOLKIDLE_SIM_CRON_SCALE="$SCALE"
  BOTMODE="--lockstep"
else
  export FOLKIDLE_TICK_SPEED="$MODE"
  BOTMODE="--speed $MODE"
fi
dotnet FolkIdle.Server.dll > "$OUT/server.log" 2>&1 &
SERVER=$!
i=0
until grep -q "Engine started" "$OUT/server.log" 2>/dev/null || [ $i -ge 180 ]; do i=$((i+1)); sleep 1; done
sleep 2
START=$(date +%s)
cd /work/bots
dotnet FolkIdle.Bots.dll $BOTMODE --mix "$MIX" --hours "$HOURS" --out "$OUT/bots" --seed 1 --allow-remote > "$OUT/bots.log" 2>&1 || true
END=$(date +%s)
touch "$OUT/bots-finished"; sleep "${HOLD:-0}"
kill $SERVER || true
echo "done: $HOURS game hours, mode $MODE, scale $SCALE, $((END-START)) wall seconds" | tee "$OUT/done.txt"
