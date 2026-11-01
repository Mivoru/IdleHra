# Recreates the throwaway database the playtest bots run against.
#
# A fast server writes GameClock timestamps from the future into its database
# (see server/FolkIdle.Server/GameClock.cs), so a bot run must never share the
# dev database the browser and exercise.mjs use. Run this before each bot run,
# then start the server with $env:FOLKIDLE_DB_CONN pointing at folkidle_bots.
#
# Usage (repo root, local Postgres container folk-idle-db running):
#   .\server\FolkIdle.Bots\new-bot-db.ps1
$ErrorActionPreference = 'Stop'
$db = 'folkidle_bots'

docker exec folk-idle-db psql -U postgres -d postgres -c "DROP DATABASE IF EXISTS $db WITH (FORCE);"
docker exec folk-idle-db psql -U postgres -d postgres -c "CREATE DATABASE $db;"

$env:FOLKIDLE_DB_CONN = "Host=localhost;Database=$db;Username=postgres;Password=postgres"
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj -- --migrate
Write-Host "Fresh $db ready. Start the server with:"
Write-Host "  `$env:FOLKIDLE_DB_CONN='$env:FOLKIDLE_DB_CONN'; `$env:FOLKIDLE_DEV_TOOLS='1'; `$env:FOLKIDLE_TICK_SPEED='20'"
