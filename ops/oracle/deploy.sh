#!/usr/bin/env bash
# Rebuild and restart the stack on the Oracle box, stamping the over-the-air
# bundle for THIS checkout. Run on the box after the SSH push:
#
#     ssh folkidle-server "bash ~/folkidle/ops/oracle/deploy.sh"
#
# Modul: THE BUNDLE VERSION MUST MOVE ON EVERY DEPLOY. The phone's updater
# downloads a bundle only when the manifest's version differs from the one it
# holds, and Caddy serves /updates/* as immutable. The version used to be
# stamped into .env once, at "Bring it up", and never again - so from
# 2026-09-12 every deploy rebuilt a zip under the same name, 1.0.464, and no
# phone that already held 1.0.464 ever fetched it. 180 commits, including the
# stopwatch and checkbox fixes, never reached a phone (task 28). Stamping here
# rather than in a README step is the point: a step people have to remember is
# how it was lost.
set -euo pipefail

REPO="$HOME/folkidle"
cd "$REPO/ops/oracle"

V="1.0.$(git -C "$REPO" rev-list --count HEAD)"
URL="https://folkidle.duckdns.org/updates/$V.zip"

touch .env
sed -i '/^FOLKIDLE_BUNDLE_/d' .env
echo "FOLKIDLE_BUNDLE_VERSION=$V" >> .env
echo "FOLKIDLE_BUNDLE_URL=$URL" >> .env
echo "deploy: stamping over-the-air bundle $V"

docker compose up -d --build

# Modul: prove the stamp landed rather than trusting .env. The app container
# needs a moment to migrate and listen, so poll the manifest for up to ~3 min.
for _ in $(seq 1 36); do
  manifest="$(curl -s -X POST https://folkidle.duckdns.org/api/v1/app/bundle \
    -H 'Content-Type: application/json' -d '{}' || true)"
  if [[ "$manifest" == *"\"version\":\"$V\""* ]]; then
    status="$(curl -s -o /dev/null -w '%{http_code}' "$URL")"
    if [[ "$status" == "200" ]]; then
      echo "deploy: OK - manifest answers $V and $URL serves 200"
      exit 0
    fi
    echo "deploy: FAILED - manifest answers $V but $URL answered $status" >&2
    exit 1
  fi
  sleep 5
done

echo "deploy: FAILED - manifest never answered $V; last answer: $manifest" >&2
exit 1
