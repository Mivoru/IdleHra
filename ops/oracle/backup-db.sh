#!/usr/bin/env bash
# Nightly dump of the game database, keeping the last seven.
#
#     crontab: 17 3 * * * bash $HOME/folkidle/ops/oracle/backup-db.sh >> $HOME/folkidle-backups/backup.log 2>&1
#
# Restore one (the app stopped first, so nothing writes mid-restore):
#
#     cd ~/folkidle/ops/oracle && docker compose stop app
#     docker compose exec -T postgres pg_restore -U folkidle -d folkidle --clean --if-exists --no-owner < ~/folkidle-backups/<file>.dump
#     docker compose start app
#
# Modul: SUPABASE USED TO DO THIS, and nothing did once Postgres moved onto this
# box (2026-09-25). A dump that lives only on this disk dies with this disk,
# so copy the folder somewhere else as well - see ops/oracle/README.md.
set -euo pipefail

DEST="$HOME/folkidle-backups"
KEEP=7
mkdir -p "$DEST"
cd "$HOME/folkidle/ops/oracle"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
tmp="$DEST/.folkidle-$stamp.dump.partial"
out="$DEST/folkidle-$stamp.dump"

# -Fc: compressed, and pg_restore can pick tables out of it. Written to a
# partial name and renamed, so a dump that dies halfway is never mistaken for
# a good one by the rotation below or by a tired human.
docker compose exec -T postgres pg_dump -U folkidle -d folkidle -Fc > "$tmp"

# A dump of a live database is never empty; a tiny file means pg_dump failed
# in a way that still exited 0 through the pipe.
size="$(stat -c %s "$tmp")"
if [[ "$size" -lt 10000 ]]; then
  echo "backup: FAILED - $tmp is only $size bytes" >&2
  exit 1
fi
mv "$tmp" "$out"

# Keep the newest $KEEP; delete the rest.
ls -1t "$DEST"/folkidle-*.dump | tail -n +"$((KEEP + 1))" | xargs -r rm --
echo "backup: OK - $out ($size bytes); $(ls -1 "$DEST"/folkidle-*.dump | wc -l) kept"
