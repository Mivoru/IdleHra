# Starts everything needed to play FolkIdle locally, in one command.
#
#   .\run-dev.ps1
#
# Then open http://localhost:5173
#
# There is no hosted link for this. The game is a WebSocket client against a
# server that owns the simulation, a Postgres database and Redis - a static
# page cannot stand in for any of that, so "try it" means running it.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host 'Starting Postgres and Redis...' -ForegroundColor Cyan
docker start folk-idle-db folk-idle-redis | Out-Null

# Modul: THIS KILL IS LOAD-BEARING. A server still holding the output
# directory makes the build succeed while silently producing a stale DLL, so
# the next run is the previous build with none of the changes in it.
Write-Host 'Stopping any running server...' -ForegroundColor Cyan
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$env:FOLKIDLE_WEB_ORIGINS = 'http://localhost:5173'
$env:FOLKIDLE_DB_CONN = 'Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres'

Write-Host 'Starting the game server on :8080...' -ForegroundColor Cyan
Start-Process powershell -ArgumentList @(
  '-NoExit', '-Command',
  "cd '$root\server'; " +
  "`$env:FOLKIDLE_WEB_ORIGINS='http://localhost:5173'; " +
  "`$env:FOLKIDLE_DB_CONN='Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres'; " +
  "dotnet run --project FolkIdle.Server/FolkIdle.Server.csproj"
)

# The server reconstructs every stored session before it opens the gateway, so
# it is not ready the moment the process exists. Polling a real endpoint is the
# only honest readiness check.
Write-Host 'Waiting for the gateway to open...' -ForegroundColor Cyan
$ready = $false
foreach ($attempt in 1..60) {
  Start-Sleep -Seconds 2
  try {
    Invoke-WebRequest -Uri 'http://localhost:8080/gamedata' -TimeoutSec 2 -UseBasicParsing | Out-Null
    $ready = $true
    break
  } catch { }
}

if (-not $ready) {
  Write-Host 'The server did not answer within two minutes. Check its window for the reason.' -ForegroundColor Red
  exit 1
}

Write-Host 'Starting the web client on :5173...' -ForegroundColor Cyan
Start-Process powershell -ArgumentList @('-NoExit', '-Command', "cd '$root\client_web'; npm run dev")

Start-Sleep -Seconds 6
Write-Host ''
Write-Host '  http://localhost:5173' -ForegroundColor Green
Write-Host ''
Write-Host '  Play as guest, or sign in as the stocked dev account:' -ForegroundColor Gray
Write-Host '    dev@folkidle.local / FolkIdleDev123!' -ForegroundColor Gray
Write-Host ''
Write-Host '  If the dev account has never been seeded, run once:' -ForegroundColor DarkGray
Write-Host '    $env:FOLKIDLE_ALLOW_DEV_SEED=1; dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj --seed-dev' -ForegroundColor DarkGray

Start-Process 'http://localhost:5173'
